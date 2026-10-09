using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Cosmos.VisualStudio.Core.Testing;
using Cosmos.VisualStudio.Services;
using Cosmos.VisualStudio.UI;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace Cosmos.VisualStudio.ToolWindows
{
    /// <summary>
    /// Runs and debugs the kernel test suites of a Cosmos source checkout
    /// (tests/Kernels/Cosmos.Kernel.Tests.*), with per-test results.
    /// </summary>
    [Guid(PackageGuids.TestsWindowString)]
    public sealed class KernelTestsWindow : ToolWindowPane
    {
        public KernelTestsWindow() : base(null)
        {
            Caption = "Cosmos Kernel Tests";
            BitmapImageMoniker = KnownMonikers.TestSuite;
            Content = new KernelTestsControl();
        }
    }

    internal sealed class KernelTestsControl : UserControl
    {
        private readonly TreeView tree;
        private readonly ComboBox archBox;
        private readonly CheckBox visualBox;
        private readonly TextBlock summary;
        private readonly TextBox details;
        private readonly Button runAll, runSelected, debugSelected, cancel, refresh;
        private CosmosPackage package;

        public KernelTestsControl()
        {
            Theme.Surface(this);

            var toolbar = new WrapPanel { Margin = new Thickness(6, 6, 6, 4) };
            archBox = new ComboBox { Width = 80, Margin = new Thickness(0, 0, 8, 4), ToolTip = "Architecture to run the suites on" };
            archBox.Items.Add("x64");
            archBox.Items.Add("arm64");
            archBox.SelectedIndex = 0;
            toolbar.Children.Add(archBox);
            runAll = ToolbarButton("Run All", KnownMonikers.RunAll, () => Run(all: true));
            runSelected = ToolbarButton("Run", KnownMonikers.RunTest, () => Run(all: false));
            debugSelected = ToolbarButton("Debug", KnownMonikers.Run, Debug);
            cancel = ToolbarButton("Cancel", KnownMonikers.Stop, () => package?.Tests.Cancel());
            refresh = ToolbarButton("Refresh", KnownMonikers.Refresh, () => package?.Tests.Refresh());
            foreach (Button b in new[] { runAll, runSelected, debugSelected, cancel, refresh })
            {
                toolbar.Children.Add(b);
            }
            visualBox = new CheckBox
            {
                Content = "Show QEMU window",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 4),
                ToolTip = "Run QEMU with a window (dev mode) instead of headless (ci mode)"
            };
            visualBox.Checked += (s, e) => SetVisualMode(true);
            visualBox.Unchecked += (s, e) => SetVisualMode(false);
            toolbar.Children.Add(visualBox);

            tree = new TreeView { BorderThickness = new Thickness(0) };
            tree.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            tree.SelectedItemChanged += (s, e) => ShowDetails();

            summary = Theme.Text("", gray: true, size: 11);
            summary.Margin = new Thickness(8, 4, 8, 4);

            details = new TextBox
            {
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MinHeight = 60,
                MaxHeight = 160,
                Margin = new Thickness(6, 0, 6, 6),
                Visibility = Visibility.Collapsed
            };

            var dock = new DockPanel();
            DockPanel.SetDock(toolbar, Dock.Top);
            DockPanel.SetDock(details, Dock.Bottom);
            DockPanel.SetDock(summary, Dock.Bottom);
            dock.Children.Add(toolbar);
            dock.Children.Add(details);
            dock.Children.Add(summary);
            dock.Children.Add(tree);
            Content = dock;

            Loaded += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                package = CosmosPackage.Instance;
                if (package == null)
                {
                    return;
                }
                visualBox.IsChecked = package.Options.TestMode == TestMode.dev;
                package.Tests.Changed += OnChanged;
                package.Tests.Refresh();
            };
            Unloaded += (s, e) =>
            {
                if (package != null)
                {
                    package.Tests.Changed -= OnChanged;
                }
            };
        }

        private static Button ToolbarButton(string text, ImageMoniker icon, Action onClick)
        {
            Button button = Theme.Button(text, onClick, icon);
            button.MinWidth = 0;
            button.Padding = new Thickness(6, 2, 8, 2);
            button.Margin = new Thickness(0, 0, 4, 4);
            return button;
        }

        private string Arch => archBox.SelectedItem as string ?? "x64";

        private void SetVisualMode(bool visual)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (package != null)
            {
                GeneralOptions options = package.Options;
                options.TestMode = visual ? TestMode.dev : TestMode.ci;
                options.SaveSettingsToStorage();
            }
        }

        private void OnChanged(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Render();
        }

        private void Render()
        {
            KernelTestService tests = package.Tests;
            string selectedSuite = SelectedSuite()?.Name;
            var expanded = new HashSet<string>(tree.Items.OfType<TreeViewItem>().Where(i => i.IsExpanded).Select(i => ((TestSuiteState)i.Tag).Name));

            tree.Items.Clear();
            foreach (TestSuiteState suite in tests.Suites)
            {
                string time = suite.Seconds != null ? $" ({suite.Seconds.Value.ToString("0.0", CultureInfo.InvariantCulture)}s)" : "";
                var item = new TreeViewItem
                {
                    Header = Row(Icon(suite.Outcome), suite.Name, Describe(suite) + time),
                    Tag = suite,
                    IsExpanded = expanded.Contains(suite.Name)
                };
                foreach (TestCaseState c in suite.Cases)
                {
                    item.Items.Add(new TreeViewItem
                    {
                        Header = Row(Icon(c.Outcome), c.Name, $"{(c.Seconds * 1000).ToString("0", CultureInfo.InvariantCulture)} ms"),
                        Tag = c
                    });
                }
                tree.Items.Add(item);
                if (suite.Name == selectedSuite)
                {
                    item.IsSelected = true;
                }
            }

            if (tests.Suites.Count == 0)
            {
                summary.Text = "No kernel test suites found. Open a Cosmos source checkout (tests/Kernels/Cosmos.Kernel.Tests.*).";
            }
            else
            {
                int passed = tests.Suites.Count(s => s.Outcome == TestOutcome.Passed);
                int failed = tests.Suites.Count(s => s.Outcome == TestOutcome.Failed);
                summary.Text = $"{tests.Suites.Count} suites · {passed} passed · {failed} failed" + (tests.IsRunning ? " · running..." : "");
            }

            bool running = tests.IsRunning;
            runAll.IsEnabled = !running && tests.Suites.Count > 0;
            runSelected.IsEnabled = !running;
            debugSelected.IsEnabled = !running;
            cancel.IsEnabled = running;
            refresh.IsEnabled = !running;
            archBox.IsEnabled = !running;
            ShowDetails();
        }

        private static string Describe(TestSuiteState suite)
        {
            switch (suite.Outcome)
            {
                case TestOutcome.Running: return "running...";
                case TestOutcome.Passed: return $"{suite.Cases.Count} passed";
                case TestOutcome.Failed: return suite.Message ?? "failed";
                case TestOutcome.Skipped: return suite.Message ?? "skipped";
                default: return "";
            }
        }

        private static ImageMoniker Icon(TestOutcome outcome)
        {
            switch (outcome)
            {
                case TestOutcome.Passed: return KnownMonikers.StatusOK;
                case TestOutcome.Failed: return KnownMonikers.StatusError;
                case TestOutcome.Skipped: return KnownMonikers.StatusWarning;
                case TestOutcome.Running: return KnownMonikers.StatusRunning;
                default: return KnownMonikers.StatusNotStarted;
            }
        }

        private static FrameworkElement Row(ImageMoniker icon, string name, string detail)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            var image = Theme.Icon(icon);
            image.Margin = new Thickness(0, 0, 6, 0);
            panel.Children.Add(image);
            panel.Children.Add(Theme.Text(name, wrap: false));
            if (!string.IsNullOrEmpty(detail))
            {
                TextBlock text = Theme.Text(detail, gray: true, wrap: false);
                text.Margin = new Thickness(8, 0, 0, 0);
                panel.Children.Add(text);
            }
            return panel;
        }

        private TestSuiteState SelectedSuite()
        {
            if (!(tree.SelectedItem is TreeViewItem item))
            {
                return null;
            }
            if (item.Tag is TestSuiteState suite)
            {
                return suite;
            }
            return (item.Parent as TreeViewItem)?.Tag as TestSuiteState;
        }

        private void ShowDetails()
        {
            string text = null;
            if (tree.SelectedItem is TreeViewItem item)
            {
                if (item.Tag is TestCaseState c && !string.IsNullOrEmpty(c.Message))
                {
                    text = c.Name + ": " + c.Message;
                }
                else if (item.Tag is TestSuiteState s && !string.IsNullOrEmpty(s.Message))
                {
                    text = s.Name + ": " + s.Message;
                }
            }
            details.Text = text ?? "";
            details.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
        }

        private void Run(bool all)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (package == null)
            {
                return;
            }
            List<TestSuiteState> targets = all
                ? package.Tests.Suites.ToList()
                : new[] { SelectedSuite() }.Where(s => s != null).ToList();
            if (targets.Count == 0)
            {
                package.ShowMessage("Select a test suite first.", Microsoft.VisualStudio.Shell.Interop.OLEMSGICON.OLEMSGICON_INFO);
                return;
            }
            package.JoinableTaskFactory.RunAsync(() => package.Tests.RunAsync(targets, Arch)).FileAndForget("Cosmos/Tests/Run");
        }

        private void Debug()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            TestSuiteState suite = SelectedSuite();
            if (package == null || suite == null)
            {
                package?.ShowMessage("Select a test suite to debug.", Microsoft.VisualStudio.Shell.Interop.OLEMSGICON.OLEMSGICON_INFO);
                return;
            }
            package.JoinableTaskFactory.RunAsync(() => package.Tests.DebugAsync(suite, Arch)).FileAndForget("Cosmos/Tests/Debug");
        }
    }
}
