using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Cosmos.VisualStudio.Core;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace Cosmos.VisualStudio.Services
{
    /// <summary>What <c>cosmos check</c> reported, for the Tools list.</summary>
    internal sealed class ToolsSnapshot
    {
        public bool Checking { get; set; }
        public bool CosmosInstalled { get; set; }
        public string CosmosVersion { get; set; }
        public bool CheckFailed { get; set; }
        public IReadOnlyList<ToolStatus> Tools { get; set; } = Array.Empty<ToolStatus>();
    }

    /// <summary>Checks and installs the Cosmos toolchain (.NET SDK, Cosmos CLI, QEMU, GDB…).</summary>
    internal sealed class ToolsService
    {
        private readonly CosmosPackage package;
        private int refreshVersion;

        public ToolsService(CosmosPackage package)
        {
            this.package = package;
        }

        public ToolsSnapshot Current { get; private set; } = new ToolsSnapshot { Checking = true };

        /// <summary>Raised on the UI thread when <see cref="Current"/> changes.</summary>
        public event EventHandler Changed;

        /// <summary>Re-runs <c>cosmos check --json</c> in the background.</summary>
        public async Task RefreshAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            int version = ++refreshVersion;
            Current = new ToolsSnapshot { Checking = true };
            Changed?.Invoke(this, EventArgs.Empty);

            ToolsSnapshot snapshot = await Task.Run(() =>
            {
                if (!CosmosTools.IsCosmosInstalled)
                {
                    return new ToolsSnapshot { CosmosInstalled = false };
                }
                List<ToolStatus> tools = CosmosTools.RefreshToolsCheck();
                return new ToolsSnapshot
                {
                    CosmosInstalled = true,
                    CheckFailed = tools == null,
                    CosmosVersion = CosmosTools.GetCosmosToolsVersion(),
                    Tools = tools ?? new List<ToolStatus>()
                };
            });

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (version == refreshVersion)
            {
                Current = snapshot;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>Prints <c>cosmos check</c> to the Build pane and offers an install when tools are missing.</summary>
        public async Task CheckAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _ = RefreshAsync();

            OutputPane pane = Panes.Build;
            pane.Show();
            pane.WriteLine("Checking development tools...");
            pane.WriteLine();

            string cosmos = CosmosTools.GetCosmosPath();
            ProcessResult result = cosmos == null
                ? null
                : await Task.Run(() => CosmosTools.Run(cosmos, new[] { "check" }, timeoutMs: 60000));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (result != null)
            {
                pane.WriteLine(LogProcessor.StripAnsi(result.Output));
            }
            else
            {
                pane.WriteLine("Cosmos Tools is not installed - run: dotnet tool install -g Cosmos.Tools");
            }

            if (result == null || result.ExitCode != 0)
            {
                if (package.Confirm("Some development tools are missing. Install them now?"))
                {
                    Install();
                }
            }
        }

        /// <summary>
        /// Opens a console running <c>cosmos install</c> (installing Cosmos.Tools
        /// first when needed); the installer is interactive and may ask for elevation.
        /// </summary>
        public void Install()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string command = CosmosTools.IsCosmosInstalled
                ? "cosmos install"
                : "dotnet tool install -g Cosmos.Tools && cosmos install";
            OpenConsole("Cosmos Tools", command);
            StatusBar.SetText("Installing Cosmos tools in a console window. Refresh the tools list when it finishes.");
        }

        public void OpenConsole(string title, string command)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var psi = new ProcessStartInfo("cmd.exe", $"/k title {title} && {command}")
            {
                UseShellExecute = false,
                CreateNoWindow = false,
                WorkingDirectory = CosmosTools.HomeDirectory
            };
            CosmosTools.ApplyEnvironment(psi);
            try
            {
                Process.Start(psi)?.Dispose();
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                package.ShowMessage("Could not open a console: " + e.Message, OLEMSGICON.OLEMSGICON_CRITICAL);
            }
        }
    }
}
