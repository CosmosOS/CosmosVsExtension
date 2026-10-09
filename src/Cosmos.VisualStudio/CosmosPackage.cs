using System;
using System.Runtime.InteropServices;
using System.Threading;
using Cosmos.VisualStudio.Core;
using Cosmos.VisualStudio.Services;
using Cosmos.VisualStudio.ToolWindows;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace Cosmos.VisualStudio
{
    /// <summary>
    /// Cosmos OS development in Visual Studio: create, build, run and debug
    /// Cosmos gen3 (NativeAOT) kernels in QEMU.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("Cosmos OS", "Create, build, run and debug Cosmos gen3 C# kernels in QEMU.", "1.0.0")]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [Guid(PackageGuids.PackageString)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExistsAndFullyLoaded_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.FolderOpened_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideOptionPage(typeof(GeneralOptions), "Cosmos OS", "General", 0, 0, true)]
    [ProvideToolWindow(typeof(CosmosExplorerWindow), Style = VsDockStyle.Tabbed, Window = ToolWindowGuids.SolutionExplorer)]
    [ProvideToolWindow(typeof(KernelPropertiesWindow), Style = VsDockStyle.MDI, Transient = true)]
    [ProvideToolWindow(typeof(KernelTestsWindow), Style = VsDockStyle.Tabbed, Window = ToolWindowGuids.Outputwindow)]
    [ProvideToolWindow(typeof(KernelThreadsWindow), Style = VsDockStyle.Tabbed, Window = ToolWindowGuids.Outputwindow)]
    [ProvideToolWindow(typeof(KernelGCWindow), Style = VsDockStyle.Tabbed, Window = ToolWindowGuids.Outputwindow)]
    [ProvideToolWindow(typeof(KernelMemoryWindow), Style = VsDockStyle.Tabbed, Window = ToolWindowGuids.Outputwindow)]
    [ProvideToolWindow(typeof(KernelMemoryMapWindow), Style = VsDockStyle.Tabbed, Window = ToolWindowGuids.Outputwindow)]
    // The kernel views come up with a Cosmos debug session and go away after it.
    [ProvideToolWindowVisibility(typeof(KernelThreadsWindow), PackageGuids.DebuggingContextString)]
    [ProvideToolWindowVisibility(typeof(KernelGCWindow), PackageGuids.DebuggingContextString)]
    [ProvideToolWindowVisibility(typeof(KernelMemoryWindow), PackageGuids.DebuggingContextString)]
    [ProvideToolWindowVisibility(typeof(KernelMemoryMapWindow), PackageGuids.DebuggingContextString)]
    public sealed class CosmosPackage : AsyncPackage
    {
        internal static CosmosPackage Instance { get; private set; }

        internal Workspace Workspace { get; private set; }
        internal KernelBuilder Builder { get; private set; }
        internal KernelLauncher Launcher { get; private set; }
        internal ToolsService Tools { get; private set; }
        internal NewProjectService NewProject { get; private set; }
        internal KernelTestService Tests { get; private set; }

        private StartCommandInterceptor startCommands;

        internal GeneralOptions Options
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return (GeneralOptions)GetDialogPage(typeof(GeneralOptions));
            }
        }

        /// <summary>Raised on the UI thread when anything the Cosmos UI shows may have changed.</summary>
        internal event EventHandler StateChanged;

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            Instance = this;

            Panes.CreateAll();
            Workspace = new Workspace();
            Builder = new KernelBuilder(this);
            Launcher = new KernelLauncher(this);
            Launcher.Initialize();
            Tools = new ToolsService(this);
            NewProject = new NewProjectService(this);
            Tests = new KernelTestService(this);

            Workspace.Changed += (s, e) => RaiseStateChanged();
            Builder.StateChanged += (s, e) => RaiseStateChanged();
            Launcher.StateChanged += (s, e) => RaiseStateChanged();

            await CosmosCommands.InitializeAsync(this);
            startCommands = new StartCommandInterceptor(this);
            startCommands.Register();

            Workspace.Refresh();
            Tests.Refresh();
            _ = Tools.RefreshAsync();
        }

        private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                KernelLiveService.Instance.Stop(null);
                // VS disposes packages on the UI thread at shutdown; skip the
                // COM cleanup otherwise rather than block on a thread switch.
                if (ThreadHelper.CheckAccess())
                {
#pragma warning disable VSTHRD010 // Guarded by CheckAccess above.
                    startCommands?.Unregister();
                    Launcher?.Stop();
#pragma warning restore VSTHRD010
                }
            }
            base.Dispose(disposing);
        }

        internal KernelTarget TargetFor(ProjectInfo project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return new KernelTarget(project.Csproj, project.Arch, Workspace.Roots);
        }

        internal void ShowMessage(string message, OLEMSGICON icon)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            VsShellUtilities.ShowMessageBox(this, message, "Cosmos OS", icon, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }

        internal bool Confirm(string question)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            int answer = VsShellUtilities.ShowMessageBox(this, question, "Cosmos OS", OLEMSGICON.OLEMSGICON_QUERY,
                OLEMSGBUTTON.OLEMSGBUTTON_YESNO, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            return answer == (int)VSConstants.MessageBoxResult.IDYES;
        }

        internal async System.Threading.Tasks.Task<T> ShowToolWindowAsync<T>() where T : ToolWindowPane
        {
            ToolWindowPane window = await ShowToolWindowAsync(typeof(T), 0, true, DisposalToken);
            return (T)window;
        }
    }
}
