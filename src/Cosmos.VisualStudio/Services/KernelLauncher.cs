using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Cosmos.VisualStudio.Core;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Shell.Settings;
using Task = System.Threading.Tasks.Task;

namespace Cosmos.VisualStudio.Services
{
    /// <summary>
    /// Runs kernels in QEMU through <c>cosmos run</c>, and debugs them by
    /// attaching Visual Studio's MI debug engine (gdb) to QEMU's gdbstub. It owns
    /// the cosmos process, so ending the run or the debug session takes QEMU down.
    /// </summary>
    internal sealed class KernelLauncher : IVsDebuggerEvents
    {
        private const int GdbPort = DebugLaunchOptions.GdbPort;
        private const int QmpPort = 4444;

        private readonly CosmosPackage package;
        private StreamingProcess cosmos;
        private bool debugging;
        private bool debuggerAttached;
        private uint debuggerCookie;

        public KernelLauncher(CosmosPackage package)
        {
            this.package = package;
        }

        public bool IsRunning => cosmos != null;

        public bool IsDebugging => debugging;

        /// <summary>Raised on the UI thread when a kernel starts or stops.</summary>
        public event EventHandler StateChanged;

        public void Initialize()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Package.GetGlobalService(typeof(SVsShellDebugger)) is IVsDebugger debugger)
            {
                debugger.AdviseDebuggerEvents(this, out debuggerCookie);
            }
        }

        public async Task RunAsync(KernelTarget target)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!await ConfirmReplaceAsync() || !await EnsureBuiltAsync(target, forDebug: false))
            {
                return;
            }

            LaunchSpec spec;
            try
            {
                spec = await Task.Run(() => CosmosLaunch.Prepare(target, debug: false));
            }
            catch (CosmosException e)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                package.ShowMessage(e.Message, OLEMSGICON.OLEMSGICON_CRITICAL);
                return;
            }
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            OutputPane pane = Panes.Output;
            pane.Clear();
            pane.Show();
            pane.WriteLine($"Running {target.Name} ({target.Arch}) via cosmos run");
            pane.WriteLine("Platform: " + CosmosTools.PlatformName);
            pane.WriteLine();
            WriteNotes(spec);
            pane.WriteLine("> " + spec.DisplayCommand);
            pane.WriteLine();

            if (StartCosmos(spec) != null)
            {
                StatusBar.SetText($"Running {target.Name} ({target.Arch}) in QEMU");
            }
        }

        public async Task DebugAsync(KernelTarget target)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!IsMIEngineInstalled())
            {
                package.ShowMessage(
                    "Debugging a Cosmos kernel uses Visual Studio's GDB debug engine (MIEngine), which is not installed.\n\n" +
                    "Open the Visual Studio Installer and add the \"Linux and embedded development with C++\" workload, then try again.",
                    OLEMSGICON.OLEMSGICON_CRITICAL);
                return;
            }
            if (!await ConfirmReplaceAsync() || !await EnsureBuiltAsync(target, forDebug: true))
            {
                return;
            }

            string elf = KernelArtifacts.ResolveKernelElf(target.RootDirs, target.ProjectDir, target.Name, target.Arch);
            if (elf == null)
            {
                package.ShowMessage($"No ELF for {target.Name} ({target.Arch}). Rebuild before debugging.", OLEMSGICON.OLEMSGICON_CRITICAL);
                return;
            }

            StatusBar.SetText("Locating gdb...");
            GdbChoice gdb = await Task.Run(() => GdbLocator.Locate(target.Arch));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (gdb == null)
            {
                package.ShowMessage(GdbLocator.MissingGdbMessage(target.Arch), OLEMSGICON.OLEMSGICON_CRITICAL);
                return;
            }

            if (await PortCheck.IsPortInUseAsync(GdbPort))
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                package.ShowMessage(
                    $"Port {GdbPort} is already in use — a previous debug session left QEMU running. " +
                    "End the stray qemu-system process (Task Manager) and try again.",
                    OLEMSGICON.OLEMSGICON_CRITICAL);
                return;
            }
            // QMP lets the kernel views read memory while the guest runs.
            bool qmpFree = !await PortCheck.IsPortInUseAsync(QmpPort);
            var passthrough = qmpFree
                ? new[] { "-qmp", $"tcp:127.0.0.1:{QmpPort},server,nowait" }
                : Array.Empty<string>();

            LaunchSpec spec;
            try
            {
                spec = await Task.Run(() => CosmosLaunch.Prepare(target, debug: true, passthrough));
            }
            catch (CosmosException e)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                package.ShowMessage(e.Message, OLEMSGICON.OLEMSGICON_CRITICAL);
                return;
            }
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            OutputPane pane = Panes.Output;
            pane.Clear();
            pane.Show();
            pane.WriteLine($"Debugging {target.Name} ({target.Arch})");
            pane.WriteLine("ELF: " + elf);
            pane.WriteLine("GDB: " + gdb.Path + (gdb.HasPython ? "" : " (no Python — pretty-printers disabled)"));
            WriteNotes(spec);
            if (!qmpFree)
            {
                pane.WriteLine($"[cosmos-debug] QMP port {QmpPort} in use — live kernel views disabled this session.");
            }
            pane.WriteLine("> " + spec.DisplayCommand);
            pane.WriteLine();

            debugging = true;
            debuggerAttached = false;
            StreamingProcess process = StartCosmos(spec);
            if (process == null)
            {
                debugging = false;
                return;
            }
            StatusBar.SetText($"Starting {target.Name} ({target.Arch}) under QEMU...");

            // Attach as soon as QEMU's gdbstub listens instead of after a fixed sleep.
            bool ready = await PortCheck.WaitForPortAsync(GdbPort, TimeSpan.FromSeconds(15), () => !process.HasExited);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!debugging)
            {
                // Stopped by the user, or cosmos exited and already reported why.
                return;
            }
            if (!ready)
            {
                EndDebugSession(stopDebugger: false);
                KillCosmos();
                package.ShowMessage($"QEMU's gdbstub on port {GdbPort} did not come up. See the Cosmos OS - Output pane.", OLEMSGICON.OLEMSGICON_CRITICAL);
                return;
            }

            if (qmpFree)
            {
                await StartLiveViewsAsync(elf);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            }
            else
            {
                KernelLiveService.Instance.StartWithoutQmp($"QMP port {QmpPort} was busy — live view unavailable this session.");
            }

            if (!debugging)
            {
                // cosmos exited while we were connecting.
                return;
            }
            UIContext.FromUIContextGuid(PackageGuids.DebuggingContext).IsActive = true;
            if (!LaunchDebugger(elf, gdb, target))
            {
                EndDebugSession(stopDebugger: false);
                KillCosmos();
            }
        }

        private static void WriteNotes(LaunchSpec spec)
        {
            foreach (string note in spec.Notes)
            {
                Panes.Output.WriteLine(note);
            }
        }

        /// <summary>Builds first when the kernel is missing or stale (per the options).</summary>
        private async Task<bool> EnsureBuiltAsync(KernelTarget target, bool forDebug)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            GeneralOptions options = package.Options;
            bool hasIso = KernelArtifacts.FindIso(target.ProjectDir, target.Arch) != null;
            string configuration = forDebug ? "Debug" : KernelBuilder.ActiveConfiguration();

            if (options.BuildBeforeLaunch)
            {
                bool outOfDate = await Task.Run(() =>
                    KernelArtifacts.IsBuildOutOfDate(target.ProjectDir, target.Arch) ||
                    (forDebug && KernelArtifacts.ResolveKernelElf(target.RootDirs, target.ProjectDir, target.Name, target.Arch) == null));
                return !outOfDate || await package.Builder.BuildAsync(target.Csproj, target.Arch, configuration);
            }

            if (hasIso)
            {
                return true;
            }
            if (!package.Confirm($"No build found for {target.Arch}. Build first?"))
            {
                return false;
            }
            return await package.Builder.BuildAsync(target.Csproj, target.Arch, configuration);
        }

        /// <summary>One kernel at a time: offers to stop the running one.</summary>
        private async Task<bool> ConfirmReplaceAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (cosmos == null && !debugging)
            {
                return true;
            }
            if (!package.Confirm("A kernel is already running. Stop it and start again?"))
            {
                return false;
            }
            Stop();
            // Give QEMU a moment to release its ports.
            for (int i = 0; i < 30 && await PortCheck.IsPortInUseAsync(GdbPort); i++)
            {
                await Task.Delay(100);
            }
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            return true;
        }

        private StreamingProcess StartCosmos(LaunchSpec spec)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var log = new LogProcessor(Panes.Output.Write);
            ProcessStartInfo psi = CosmosTools.CreateStartInfo(spec.CosmosPath, spec.Arguments, spec.Target.ProjectDir);
            StreamingProcess process;
            try
            {
                process = StreamingProcess.Start(psi, log.Append);
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception || e is CosmosException)
            {
                Panes.Output.WriteLine("Error: " + e.Message);
                package.ShowMessage("cosmos run error: " + e.Message, OLEMSGICON.OLEMSGICON_CRITICAL);
                return null;
            }
            cosmos = process;
            StateChanged?.Invoke(this, EventArgs.Empty);
            _ = WatchExitAsync(process, log);
            return process;
        }

        private async Task WatchExitAsync(StreamingProcess process, LogProcessor log)
        {
            // Completion is a TaskCompletionSource that never needs the UI thread.
#pragma warning disable VSTHRD003
            int code = await process.Completion.ConfigureAwait(false);
#pragma warning restore VSTHRD003
            log.Flush();
            Panes.Output.WriteLine();
            Panes.Output.WriteLine($"cosmos run exited with code {code}");

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (cosmos != process)
            {
                return;
            }
            cosmos = null;
            process.Dispose();
            if (debugging)
            {
                // QEMU is gone (window closed, kernel halted): end the debug session too.
                EndDebugSession(stopDebugger: true);
            }
            StatusBar.SetText($"Kernel exited with code {code}");
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Stops the running kernel and, if debugging, the debug session.</summary>
        public void Stop()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (debugging)
            {
                EndDebugSession(stopDebugger: true);
            }
            KillCosmos();
        }

        private void KillCosmos()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            StreamingProcess process = cosmos;
            if (process == null)
            {
                return;
            }
            // cosmos holds QEMU in a kill-on-close job, so taking its tree down
            // takes QEMU with it.
            process.KillTree();
        }

        private async Task StartLiveViewsAsync(string elf)
        {
            var qmp = new QmpClient("127.0.0.1", QmpPort);
            try
            {
                bool listening = await PortCheck.WaitForPortAsync(QmpPort, TimeSpan.FromSeconds(5), null).ConfigureAwait(false);
                if (!listening)
                {
                    throw new IOException("QMP socket did not open");
                }
                await qmp.ConnectAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                Panes.Output.WriteLine($"[cosmos-debug] QMP connected on 127.0.0.1:{QmpPort}");
                // Resolve the snapshot statics from the ELF up front so the views
                // never need a debugger function call to find their buffers.
                Dictionary<string, ulong> statics = ElfSymbols.Resolve(elf,
                    new[] { ThreadSnapshot.StaticsSymbol, GCStats.StaticsSymbol, MemoryStats.StaticsSymbol });
                KernelLiveService.Instance.Start(qmp, statics);
            }
            catch (Exception e) when (e is IOException || e is TimeoutException || e is System.Net.Sockets.SocketException || e is UnauthorizedAccessException)
            {
                Panes.Output.WriteLine("[cosmos-debug] QMP unavailable: " + e.Message);
                qmp.Dispose();
                KernelLiveService.Instance.StartWithoutQmp("QMP unavailable — live view disabled this session.");
            }
        }

        private bool IsMIEngineInstalled()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                SettingsStore store = new ShellSettingsManager(package).GetReadOnlySettingsStore(SettingsScope.Configuration);
                return store.CollectionExists(@"AD7Metrics\Engine\{" + PackageGuids.MIEngine.ToString("D") + "}");
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        /// <summary>
        /// Hands the session to MIEngine: gdb loads the ELF's symbols, connects to
        /// QEMU's gdbstub and continues the guest (QEMU waits on -S until then).
        /// </summary>
        private bool LaunchDebugger(string elf, GdbChoice gdb, KernelTarget target)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(Package.GetGlobalService(typeof(SVsShellDebugger)) is IVsDebugger4 debugger))
            {
                package.ShowMessage("The Visual Studio debugger is not available.", OLEMSGICON.OLEMSGICON_CRITICAL);
                return false;
            }

            var info = new VsDebugTargetInfo4[1];
            info[0].dlo = (uint)DEBUG_LAUNCH_OPERATION.DLO_CreateProcess;
            info[0].bstrExe = elf;
            info[0].bstrCurDir = target.ProjectDir;
            info[0].bstrOptions = DebugLaunchOptions.Build(elf, gdb, target,
                Path.Combine(Path.GetDirectoryName(typeof(KernelLauncher).Assembly.Location), "Resources", "gdb", "cosmos_prettyprint.py"));
            info[0].guidLaunchDebugEngine = PackageGuids.MIEngine;
            info[0].LaunchFlags = (uint)__VSDBGLAUNCHFLAGS.DBGLAUNCH_StopDebuggingOnEnd;
            var results = new VsDebugTargetProcessInfo[1];
            try
            {
                debugger.LaunchDebugTargets4(1, info, results);
                return true;
            }
            catch (Exception e) when (e is COMException || e is ArgumentException)
            {
                package.ShowMessage("Failed to start the debugger: " + e.Message, OLEMSGICON.OLEMSGICON_CRITICAL);
                return false;
            }
        }

        private void EndDebugSession(bool stopDebugger)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!debugging)
            {
                return;
            }
            debugging = false;
            debuggerAttached = false;
            KernelLiveService.Instance.Stop("Debug session ended.");
            UIContext.FromUIContextGuid(PackageGuids.DebuggingContext).IsActive = false;
            if (stopDebugger && CurrentMode() != DBGMODE.DBGMODE_Design)
            {
                try
                {
                    (Package.GetGlobalService(typeof(SDTE)) as DTE)?.Debugger.Stop(false);
                }
                catch (COMException)
                {
                    // Already stopping.
                }
            }
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        private static DBGMODE CurrentMode()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var mode = new DBGMODE[1];
            if (Package.GetGlobalService(typeof(SVsShellDebugger)) is IVsDebugger debugger &&
                debugger.GetMode(mode) == VSConstants.S_OK)
            {
                return mode[0] & ~DBGMODE.DBGMODE_EncMask;
            }
            return DBGMODE.DBGMODE_Design;
        }

        public static bool IsDebuggerIdle()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return CurrentMode() == DBGMODE.DBGMODE_Design;
        }

        int IVsDebuggerEvents.OnModeChange(DBGMODE dbgmodeNew)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!debugging)
            {
                return VSConstants.S_OK;
            }
            DBGMODE mode = dbgmodeNew & ~DBGMODE.DBGMODE_EncMask;
            if (mode == DBGMODE.DBGMODE_Design)
            {
                if (debuggerAttached)
                {
                    // The user stopped debugging (or gdb lost QEMU): take QEMU down too.
                    EndDebugSession(stopDebugger: false);
                    KillCosmos();
                }
            }
            else
            {
                debuggerAttached = true;
                if (mode == DBGMODE.DBGMODE_Break)
                {
                    KernelLiveService.Instance.OnDebuggerBreak();
                }
            }
            return VSConstants.S_OK;
        }
    }
}
