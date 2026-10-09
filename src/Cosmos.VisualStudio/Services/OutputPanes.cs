using System;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace Cosmos.VisualStudio.Services
{
    /// <summary>A custom pane of the Output window.</summary>
    internal sealed class OutputPane
    {
        private readonly Guid guid;
        private readonly string title;
        private IVsOutputWindowPane pane;

        public OutputPane(Guid guid, string title)
        {
            this.guid = guid;
            this.title = title;
        }

        private IVsOutputWindowPane Pane
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (pane == null && Package.GetGlobalService(typeof(SVsOutputWindow)) is IVsOutputWindow output)
                {
                    Guid id = guid;
                    output.CreatePane(ref id, title, 1, 0);
                    output.GetPane(ref id, out pane);
                }
                return pane;
            }
        }

        /// <summary>Creates the pane up front so background writers never need the UI thread.</summary>
        public void EnsureCreated()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _ = Pane;
        }

        /// <summary>Appends text from any thread.</summary>
        public void Write(string text)
        {
            // OutputStringThreadSafe is the one pane member meant for any thread.
#pragma warning disable VSTHRD010
            pane?.OutputStringThreadSafe(text);
#pragma warning restore VSTHRD010
        }

        public void WriteLine(string text = "") => Write(text + "\n");

        public void Clear()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Pane?.Clear();
        }

        /// <summary>Brings the Output window up on this pane.</summary>
        public void Show()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Pane?.Activate();
            if (Package.GetGlobalService(typeof(SVsUIShell)) is IVsUIShell shell)
            {
                Guid outputWindow = new Guid(ToolWindowGuids.Outputwindow);
                if (shell.FindToolWindow((uint)__VSFINDTOOLWIN.FTW_fForceCreate, ref outputWindow, out IVsWindowFrame frame) == VSConstants.S_OK)
                {
                    frame?.ShowNoActivate();
                }
            }
        }
    }

    internal static class Panes
    {
        public static OutputPane Build { get; } = new OutputPane(PackageGuids.BuildPane, "Cosmos OS - Build");
        public static OutputPane Output { get; } = new OutputPane(PackageGuids.OutputPane, "Cosmos OS - Output");
        public static OutputPane Tests { get; } = new OutputPane(PackageGuids.TestsPane, "Cosmos OS - Tests");

        public static void CreateAll()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Build.EnsureCreated();
            Output.EnsureCreated();
            Tests.EnsureCreated();
        }
    }

    /// <summary>The Visual Studio status bar.</summary>
    internal static class StatusBar
    {
        private static object buildIcon = (short)Constants.SBAI_Build;

        public static void SetText(string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Package.GetGlobalService(typeof(SVsStatusbar)) is IVsStatusbar bar)
            {
                bar.IsFrozen(out int frozen);
                if (frozen == 0)
                {
                    bar.SetText(text);
                }
            }
        }

        public static void Animate(bool on)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Package.GetGlobalService(typeof(SVsStatusbar)) is IVsStatusbar bar)
            {
                bar.Animation(on ? 1 : 0, ref buildIcon);
            }
        }
    }
}
