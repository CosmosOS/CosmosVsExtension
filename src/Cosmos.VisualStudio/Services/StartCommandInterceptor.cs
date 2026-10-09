using System;
using Cosmos.VisualStudio.Core;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace Cosmos.VisualStudio.Services
{
    /// <summary>
    /// Routes Start Debugging (F5) and Start Without Debugging (Ctrl+F5) to the
    /// kernel when the startup project is a Cosmos kernel. Without this, Visual
    /// Studio would try to launch the kernel's managed exe on Windows.
    /// </summary>
    internal sealed class StartCommandInterceptor : IOleCommandTarget
    {
        private static readonly Guid Std97 = VSConstants.GUID_VSStandardCommandSet97;
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(2);

        private readonly CosmosPackage package;
        private uint cookie;
        private ProjectInfo cachedProject;
        private DateTime cachedAt = DateTime.MinValue;

        public StartCommandInterceptor(CosmosPackage package)
        {
            this.package = package;
        }

        public void Register()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Package.GetGlobalService(typeof(SVsRegisterPriorityCommandTarget)) is IVsRegisterPriorityCommandTarget register)
            {
                register.RegisterPriorityCommandTarget(0, this, out cookie);
            }
            package.Workspace.Changed += (s, e) => cachedAt = DateTime.MinValue;
        }

        private static bool IsStartCommand(Guid group, uint id) =>
            group == Std97 &&
            (id == (uint)VSConstants.VSStd97CmdID.Start || id == (uint)VSConstants.VSStd97CmdID.StartNoDebug);

        // QueryStatus runs on every idle tick, so the startup project lookup is cached briefly.
        private ProjectInfo LaunchProject()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!package.Options.HandleStartCommands || !KernelLauncher.IsDebuggerIdle())
            {
                return null;
            }
            if (DateTime.UtcNow - cachedAt > CacheLifetime)
            {
                cachedProject = package.Workspace.FindLaunchProject();
                cachedAt = DateTime.UtcNow;
            }
            return cachedProject;
        }

        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (cCmds == 1 && IsStartCommand(pguidCmdGroup, prgCmds[0].cmdID) && LaunchProject() != null)
            {
                prgCmds[0].cmdf = (uint)(OLECMDF.OLECMDF_SUPPORTED | OLECMDF.OLECMDF_ENABLED);
                return VSConstants.S_OK;
            }
            return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
        }

        public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!IsStartCommand(pguidCmdGroup, nCmdID))
            {
                return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
            }
            // Re-resolve rather than trusting the cache: this is the real launch.
            cachedAt = DateTime.MinValue;
            ProjectInfo project = LaunchProject();
            if (project == null)
            {
                return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
            }

            KernelTarget target = package.TargetFor(project);
            bool debug = nCmdID == (uint)VSConstants.VSStd97CmdID.Start;
            package.JoinableTaskFactory.RunAsync(() => debug ? package.Launcher.DebugAsync(target) : package.Launcher.RunAsync(target)).FileAndForget("Cosmos/StartCommand");
            return VSConstants.S_OK;
        }

        public void Unregister()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (cookie != 0 && Package.GetGlobalService(typeof(SVsRegisterPriorityCommandTarget)) is IVsRegisterPriorityCommandTarget register)
            {
                register.UnregisterPriorityCommandTarget(cookie);
                cookie = 0;
            }
        }
    }
}
