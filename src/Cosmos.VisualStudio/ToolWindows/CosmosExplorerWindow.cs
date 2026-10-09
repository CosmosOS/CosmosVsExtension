using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Cosmos.VisualStudio.Core;
using Cosmos.VisualStudio.Services;
using Cosmos.VisualStudio.UI;
using EnvDTE;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace Cosmos.VisualStudio.ToolWindows
{
    /// <summary>
    /// The Cosmos OS window: a welcome page without a kernel project, otherwise
    /// the kernel's actions and the state of the toolchain.
    /// </summary>
    [Guid(PackageGuids.ExplorerWindowString)]
    public sealed class CosmosExplorerWindow : ToolWindowPane
    {
        public CosmosExplorerWindow() : base(null)
        {
            Caption = "Cosmos OS";
            BitmapImageMoniker = KnownMonikers.VirtualMachine;
            Content = new CosmosExplorerControl();
        }
    }

    internal sealed class CosmosExplorerControl : UserControl
    {
        private readonly StackPanel body = new StackPanel { Margin = new Thickness(10, 4, 10, 12) };
        private CosmosPackage package;

        public CosmosExplorerControl()
        {
            Theme.Surface(this);
            Content = new ScrollViewer
            {
                Content = body,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            Loaded += (s, e) => Attach();
            Unloaded += (s, e) => Detach();
        }

        private void Attach()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            package = CosmosPackage.Instance;
            if (package == null)
            {
                body.Children.Clear();
                body.Children.Add(Theme.Text("Loading Cosmos...", gray: true));
                return;
            }
            package.StateChanged += OnChanged;
            package.Tools.Changed += OnChanged;
            Render();
        }

        private void Detach()
        {
            if (package != null)
            {
                package.StateChanged -= OnChanged;
                package.Tools.Changed -= OnChanged;
            }
        }

        private void OnChanged(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Render();
        }

        private void Invoke(int commandId)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (((IServiceProvider)package).GetService(typeof(IMenuCommandService)) is OleMenuCommandService commands)
            {
                commands.GlobalInvoke(new CommandID(PackageGuids.CommandSet, commandId));
            }
        }

        private bool rendering;

        private void Render()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            // Reading CurrentProject can publish a change, which asks for a render
            // while this one is under way; this render already sees the new state.
            if (rendering)
            {
                return;
            }
            rendering = true;
            try
            {
                RenderCore();
            }
            finally
            {
                rendering = false;
            }
        }

        private void RenderCore()
        {
            ProjectInfo project = package.Workspace.CurrentProject;
            body.Children.Clear();
            if (project == null)
            {
                RenderWelcome();
            }
            else
            {
                RenderProject(project);
            }
            RenderTools();
        }

        private void RenderWelcome()
        {
            var welcome = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            welcome.Children.Add(Theme.Text("Welcome to Cosmos OS Development!", bold: true, size: 14));
            TextBlock line = Theme.Text("Create your first bare-metal C# kernel.", gray: true);
            line.Margin = new Thickness(0, 6, 0, 10);
            welcome.Children.Add(line);
            Button create = Theme.Button("Create Kernel Project", () => Invoke(PackageIds.NewProject), KnownMonikers.AddItem);
            create.HorizontalAlignment = HorizontalAlignment.Stretch;
            welcome.Children.Add(create);

            TextBlock open = Theme.Text("Or open an existing Cosmos project.", gray: true);
            open.Margin = new Thickness(0, 14, 0, 8);
            welcome.Children.Add(open);
            var buttons = new UniformGrid2();
            buttons.Add(Theme.Button("Open Folder...", () => ExecuteVsCommand("File.OpenFolder"), KnownMonikers.OpenFolder));
            buttons.Add(Theme.Button("Open Project...", () => ExecuteVsCommand("File.OpenProject"), KnownMonikers.OpenFolder));
            welcome.Children.Add(buttons.Panel);
            body.Children.Add(welcome);
        }

        private void RenderProject(ProjectInfo project)
        {
            string label = ProjectInfo.ArchLabel(project.Arch);
            body.Children.Add(Theme.SectionHeader("Project"));

            var title = new StackPanel { Margin = new Thickness(6, 2, 0, 6) };
            title.Children.Add(Theme.Text(project.Name, bold: true, wrap: false));
            title.Children.Add(Theme.Text($"{ProjectInfo.ArchDescription(project.Arch)} · {project.Csproj}", gray: true, size: 11, wrap: false));
            body.Children.Add(title);

            bool busy = package.Builder.IsBuilding;
            Button Row(Microsoft.VisualStudio.Imaging.Interop.ImageMoniker icon, string text, string detail, int command, bool enabled = true)
            {
                Button row = Theme.ActionRow(icon, text, detail, () => Invoke(command));
                row.IsEnabled = enabled;
                return row;
            }

            body.Children.Add(Row(KnownMonikers.Property, "Properties", "Edit project settings", PackageIds.Properties));
            body.Children.Add(Row(KnownMonikers.BuildSelection, "Build", "Build for " + ProjectInfo.ArchDescription(project.Arch), PackageIds.Build, !busy));
            body.Children.Add(Row(KnownMonikers.RunOutline, "Run", $"Run in QEMU ({label})", PackageIds.Run, !busy));
            body.Children.Add(Row(KnownMonikers.Run, "Debug", $"Debug with GDB ({label})", PackageIds.Debug, !busy));
            if (busy || package.Launcher.IsRunning || package.Launcher.IsDebugging)
            {
                string what = busy ? "Cancel the build" : package.Launcher.IsDebugging ? "End the debug session and QEMU" : "Stop QEMU";
                body.Children.Add(Row(KnownMonikers.Stop, "Stop", what, PackageIds.Stop));
            }
            body.Children.Add(Row(KnownMonikers.CleanData, "Clean", "Remove build outputs", PackageIds.Clean, !busy));
            body.Children.Add(Row(KnownMonikers.TestSuite, "Kernel Tests", "Run the kernel test suites", PackageIds.ShowTests));
        }

        private void RenderTools()
        {
            Button refresh = new Button
            {
                Content = Theme.Icon(KnownMonikers.Refresh),
                ToolTip = "Refresh",
                Padding = new Thickness(2),
                MinWidth = 0,
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0)
            };
            refresh.Click += (s, e) => _ = package.Tools.RefreshAsync();
            body.Children.Add(Theme.SectionHeader("Tools", refresh));

            ToolsSnapshot tools = package.Tools.Current;
            if (tools.Checking)
            {
                TextBlock checking = Theme.Text("Checking tools...", gray: true);
                checking.Margin = new Thickness(6, 2, 0, 2);
                body.Children.Add(checking);
            }
            else if (!tools.CosmosInstalled)
            {
                body.Children.Add(ToolRow("Cosmos Tools", false, "Not installed - run: dotnet tool install -g Cosmos.Tools"));
            }
            else if (tools.CheckFailed)
            {
                body.Children.Add(ToolRow("Cosmos Tools", false, "Check failed - reinstall Cosmos.Tools"));
            }
            else
            {
                body.Children.Add(ToolRow("Cosmos Tools", true, tools.CosmosVersion ?? "Installed"));
                foreach (ToolStatus tool in tools.Tools)
                {
                    body.Children.Add(ToolRow(tool.DisplayName, tool.Found, tool.Found ? tool.Version ?? "Installed" : "Not installed"));
                }
            }

            var buttons = new UniformGrid2 { Margin = new Thickness(0, 10, 0, 0) };
            buttons.Add(Theme.Button("Check Tools", () => Invoke(PackageIds.CheckTools), KnownMonikers.Checklist));
            buttons.Add(Theme.Button("Install Tools", () => Invoke(PackageIds.InstallTools), KnownMonikers.Download));
            body.Children.Add(buttons.Panel);
        }

        private static FrameworkElement ToolRow(string name, bool installed, string detail)
        {
            var row = new DockPanel { Margin = new Thickness(6, 2, 0, 2), ToolTip = installed ? $"{name}: {detail}" : $"{name} is not installed" };
            var icon = Theme.Icon(installed ? KnownMonikers.StatusOK : KnownMonikers.StatusError);
            icon.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            TextBlock text = Theme.Text(name, wrap: false);
            DockPanel.SetDock(text, Dock.Left);
            row.Children.Add(text);
            TextBlock version = Theme.Text(detail, gray: true, wrap: false);
            version.Margin = new Thickness(8, 0, 0, 0);
            row.Children.Add(version);
            return row;
        }

        private static void ExecuteVsCommand(string name)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                (Package.GetGlobalService(typeof(SDTE)) as DTE)?.ExecuteCommand(name);
            }
            catch (COMException)
            {
                // The command is unavailable in this state.
            }
        }

        /// <summary>Two buttons side by side, sharing the width.</summary>
        private sealed class UniformGrid2
        {
            public Grid Panel { get; } = new Grid();

            public Thickness Margin
            {
                set => Panel.Margin = value;
            }

            public void Add(Button button)
            {
                int column = Panel.ColumnDefinitions.Count;
                Panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                button.HorizontalAlignment = HorizontalAlignment.Stretch;
                button.Margin = new Thickness(column == 0 ? 0 : 6, 0, 0, 0);
                Grid.SetColumn(button, column);
                Panel.Children.Add(button);
            }
        }
    }
}
