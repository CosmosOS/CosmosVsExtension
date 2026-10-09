using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace Cosmos.VisualStudio.UI
{
    /// <summary>Asks for the new kernel's name, architecture and location.</summary>
    internal sealed class NewProjectDialog : DialogWindow
    {
        private static readonly Regex ValidName = new Regex("^[a-zA-Z][a-zA-Z0-9_]*$");

        private readonly TextBox nameBox;
        private readonly ComboBox archBox;
        private readonly TextBox locationBox;
        private readonly CheckBox subfolderBox;
        private readonly CheckBox addToSolutionBox;
        private readonly TextBlock errorText;
        private readonly TextBlock previewText;
        private readonly Button createButton;

        public NewProjectDialog(string defaultLocation, string defaultArch, bool solutionOpen)
        {
            Title = "New Cosmos Kernel Project";
            Width = 560;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            HasMaximizeButton = false;
            HasMinimizeButton = false;
            HasHelpButton = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var root = new StackPanel { Margin = new Thickness(16) };
            Theme.Surface(root);
            root.SetResourceReference(BackgroundProperty, ThemedDialogColors.WindowPanelBrushKey);
            SetResourceReference(BackgroundProperty, ThemedDialogColors.WindowPanelBrushKey);

            TextBlock intro = Theme.Text("Create a bare-metal C# kernel from the cosmos-kernel template.", gray: true);
            intro.Margin = new Thickness(0, 0, 0, 14);
            root.Children.Add(intro);

            nameBox = new TextBox { Text = "MyKernel" };
            nameBox.TextChanged += (s, e) => Validate();
            root.Children.Add(Theme.Field("Project name", nameBox));

            archBox = new ComboBox();
            archBox.Items.Add(new ComboBoxItem { Content = "x64 — Intel/AMD 64-bit (recommended for most users)", Tag = "x64" });
            archBox.Items.Add(new ComboBoxItem { Content = "arm64 — ARM 64-bit (Raspberry Pi, Apple Silicon VMs)", Tag = "arm64" });
            archBox.SelectedIndex = defaultArch == "arm64" ? 1 : 0;
            root.Children.Add(Theme.Field("Target architecture", archBox));

            var locationRow = new DockPanel();
            Button browse = Theme.Button("Browse...", Browse);
            browse.Margin = new Thickness(6, 0, 0, 0);
            DockPanel.SetDock(browse, Dock.Right);
            locationRow.Children.Add(browse);
            locationBox = new TextBox { Text = defaultLocation ?? "" };
            locationBox.TextChanged += (s, e) => Validate();
            locationRow.Children.Add(locationBox);
            root.Children.Add(Theme.Field("Location", locationRow));

            subfolderBox = new CheckBox { Content = "Place the project in a new folder named after it", IsChecked = true, Margin = new Thickness(0, 0, 0, 6) };
            subfolderBox.Checked += (s, e) => Validate();
            subfolderBox.Unchecked += (s, e) => Validate();
            root.Children.Add(subfolderBox);

            addToSolutionBox = new CheckBox
            {
                Content = "Add to the current solution",
                IsChecked = false,
                Visibility = solutionOpen ? Visibility.Visible : Visibility.Collapsed,
                Margin = new Thickness(0, 0, 0, 6)
            };
            root.Children.Add(addToSolutionBox);

            previewText = Theme.Text("", gray: true, size: 11.5);
            previewText.Margin = new Thickness(0, 6, 0, 0);
            root.Children.Add(previewText);

            errorText = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
            errorText.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowValidationErrorTextBrushKey);
            root.Children.Add(errorText);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
            createButton = Theme.Button("Create", Create);
            createButton.IsDefault = true;
            Button cancel = Theme.Button("Cancel", () => DialogResult = false);
            cancel.IsCancel = true;
            cancel.Margin = new Thickness(8, 0, 0, 0);
            buttons.Children.Add(createButton);
            buttons.Children.Add(cancel);
            root.Children.Add(buttons);

            Content = root;
            Loaded += (s, e) =>
            {
                nameBox.Focus();
                nameBox.SelectAll();
            };
            Validate();
        }

        public string ProjectName => nameBox.Text.Trim();

        public string Architecture => (archBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "x64";

        public bool AddToSolution => addToSolutionBox.Visibility == Visibility.Visible && addToSolutionBox.IsChecked == true;

        public string ProjectDirectory
        {
            get
            {
                string location = locationBox.Text.Trim();
                return subfolderBox.IsChecked == true ? Path.Combine(location, ProjectName) : location;
            }
        }

        private string Error()
        {
            if (ProjectName.Length == 0)
            {
                return "Project name is required.";
            }
            if (!ValidName.IsMatch(ProjectName))
            {
                return "Project name must start with a letter and contain only letters, numbers, and underscores.";
            }
            string location = locationBox.Text.Trim();
            if (location.Length == 0)
            {
                return "Choose a location.";
            }
            try
            {
                if (!Path.IsPathRooted(location))
                {
                    return "The location must be a full path.";
                }
                Path.GetFullPath(location);
            }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException)
            {
                return "The location is not a valid path.";
            }
            return null;
        }

        private void Validate()
        {
            string error = Error();
            createButton.IsEnabled = error == null;
            errorText.Text = error ?? "";
            errorText.Visibility = error == null ? Visibility.Collapsed : Visibility.Visible;
            previewText.Text = error == null ? "The project will be created in " + ProjectDirectory : "";
        }

        private void Create()
        {
            if (Error() != null)
            {
                return;
            }
            string dir = ProjectDirectory;
            if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
            {
                MessageBoxResult answer = MessageBox.Show(this,
                    $"The folder {dir} already exists and is not empty. Template files may overwrite existing ones. Continue?",
                    Title, MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes)
                {
                    return;
                }
            }
            DialogResult = true;
        }

        private void Browse()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!(Package.GetGlobalService(typeof(SVsUIShell)) is IVsUIShell shell))
            {
                return;
            }
            const int maxPath = 1024;
            IntPtr buffer = Marshal.AllocCoTaskMem((maxPath + 1) * sizeof(char));
            try
            {
                string initial = Directory.Exists(locationBox.Text) ? locationBox.Text : "";
                var info = new VSBROWSEINFOW[1];
                info[0].lStructSize = (uint)Marshal.SizeOf(typeof(VSBROWSEINFOW));
                info[0].hwndOwner = new WindowInteropHelper(this).Handle;
                info[0].pwzDlgTitle = "Select project location";
                info[0].pwzInitialDir = initial;
                info[0].nMaxDirName = maxPath;
                info[0].pwzDirName = buffer;
                if (shell.GetDirectoryViaBrowseDlg(info) == VSConstants.S_OK)
                {
                    string chosen = Marshal.PtrToStringUni(buffer);
                    if (!string.IsNullOrEmpty(chosen))
                    {
                        locationBox.Text = chosen;
                    }
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(buffer);
            }
        }
    }
}
