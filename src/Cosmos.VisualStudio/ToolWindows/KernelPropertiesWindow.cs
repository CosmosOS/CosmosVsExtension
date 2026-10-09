using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Cosmos.VisualStudio.Core;
using Cosmos.VisualStudio.UI;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace Cosmos.VisualStudio.ToolWindows
{
    /// <summary>
    /// The kernel's properties, edited in a document tab and saved as you type,
    /// for a kernel opened as a folder. A kernel loaded as a project edits them
    /// in Visual Studio's Project Properties instead (ProjectSystem/).
    /// </summary>
    [Guid(PackageGuids.PropertiesWindowString)]
    public sealed class KernelPropertiesWindow : ToolWindowPane
    {
        private readonly KernelPropertiesControl control = new KernelPropertiesControl();

        public KernelPropertiesWindow() : base(null)
        {
            Caption = "Kernel Properties";
            BitmapImageMoniker = KnownMonikers.Property;
            Content = control;
        }

        internal void Load(ProjectInfo project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Caption = project.Name + " - Properties";
            control.Load(project);
        }
    }

    internal sealed class KernelPropertiesControl : UserControl
    {
        private readonly DispatcherTimer saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        private readonly DispatcherTimer qemuSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        private readonly DispatcherTimer savedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        private readonly Dictionary<string, FrameworkElement> featureFields = new Dictionary<string, FrameworkElement>();
        private readonly HashSet<string> collapsedSections = new HashSet<string> { "Advanced" };

        private ProjectInfo project;
        private ProjectProperties props;
        private TextBlock savedText;
        private StackPanel diskList;

        public KernelPropertiesControl()
        {
            Theme.Surface(this);
            saveTimer.Tick += (s, e) => { saveTimer.Stop(); Save(); };
            qemuSaveTimer.Tick += (s, e) => { qemuSaveTimer.Stop(); SaveQemu(); };
            savedTimer.Tick += (s, e) =>
            {
                savedTimer.Stop();
                if (savedText != null) savedText.Visibility = Visibility.Hidden;
            };
            Content = Theme.Text("Open a Cosmos kernel project, then choose Cosmos > Kernel Properties.", gray: true);
            Padding = new Thickness(24);
        }

        public void Load(ProjectInfo info)
        {
            project = info;
            try
            {
                props = ProjectConfig.Parse(info.Csproj);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Content = Theme.Text("Could not read " + info.Csproj + ": " + e.Message, gray: true);
                return;
            }
            Render();
        }

        private void Render()
        {
            Padding = new Thickness(0);
            var page = new StackPanel { Margin = new Thickness(28, 24, 28, 32), MaxWidth = 780, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 420 };
            page.Children.Add(Header());
            page.Children.Add(Section("General", General()));
            page.Children.Add(Section("Features", Features()));
            page.Children.Add(Section("Advanced", Advanced()));
            page.Children.Add(Section("QEMU Configuration", Qemu()));
            page.Children.Add(Section("Packages", Packages()));
            UpdateFeatureVisibility();

            var scroll = new ScrollViewer
            {
                Content = page,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            Content = scroll;
        }

        private FrameworkElement Header()
        {
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titles = new StackPanel();
            titles.Children.Add(Theme.Text(props.Name, size: 24, bold: true));
            titles.Children.Add(Theme.Text("Cosmos Kernel Project", gray: true));
            grid.Children.Add(titles);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
            savedText = new TextBlock
            {
                Text = "Saved",
                Foreground = new SolidColorBrush(Color.FromRgb(0x3f, 0xb9, 0x50)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0),
                Visibility = Visibility.Hidden
            };
            actions.Children.Add(savedText);
            actions.Children.Add(Theme.Button("Edit .csproj", OpenCsproj));
            Grid.SetColumn(actions, 1);
            grid.Children.Add(actions);
            return grid;
        }

        private FrameworkElement Section(string title, FrameworkElement body)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            var chevron = Theme.Text("", gray: true, size: 10, wrap: false);
            chevron.Margin = new Thickness(0, 0, 6, 0);
            chevron.VerticalAlignment = VerticalAlignment.Center;
            var titleText = Theme.Text(title.ToUpperInvariant(), gray: true, size: 11, bold: true, wrap: false);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(chevron);
            row.Children.Add(titleText);

            var header = new Border
            {
                Child = row,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(0, 4, 0, 6),
                Margin = new Thickness(0, 0, 0, 14),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand
            };
            header.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);

            void Apply()
            {
                bool collapsed = collapsedSections.Contains(title);
                chevron.Text = collapsed ? "▶" : "▼";
                body.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
            }
            header.MouseLeftButtonUp += (s, e) =>
            {
                if (!collapsedSections.Remove(title))
                {
                    collapsedSections.Add(title);
                }
                Apply();
            };
            Apply();

            panel.Children.Add(header);
            panel.Children.Add(body);
            return panel;
        }

        private FrameworkElement General()
        {
            var panel = new StackPanel();

            ComboBox framework = Combo(new[] { new QemuChoice("net10.0", ".NET 10") }, props.TargetFramework);
            framework.SelectionChanged += (s, e) => { props.TargetFramework = Selected(framework) ?? props.TargetFramework; Save(); };
            panel.Children.Add(Theme.Field(".NET Version", framework));

            ComboBox arch = Combo(KernelConfigSettings.Architectures, props.TargetArch);
            arch.SelectionChanged += (s, e) =>
            {
                string chosen = Selected(arch);
                if (chosen == null || chosen == props.TargetArch)
                {
                    return;
                }
                props.TargetArch = chosen;
                Save();
                // Machine type, CPU and device choices depend on the architecture:
                // reload so they are re-validated against the new one.
                CosmosPackage.Instance.JoinableTaskFactory.RunAsync(async () =>
                {
                    await CosmosPackage.Instance.JoinableTaskFactory.SwitchToMainThreadAsync(alwaysYield: true);
                    Load(project);
                }).FileAndForget("Cosmos/Properties/Reload");
            };
            panel.Children.Add(Theme.Field("Target Architecture", arch));

            TextBox kernelClass = TextField(props.KernelClass, v => { props.KernelClass = v; QueueSave(); });
            panel.Children.Add(Theme.Field("Kernel Entry Class", kernelClass, "Fully qualified class name (e.g., MyKernel.Kernel)"));
            return panel;
        }

        private FrameworkElement Features()
        {
            var panel = new StackPanel();
            void Add(string key, string label, string hint, bool value, Action<bool> set)
            {
                var text = new StackPanel();
                text.Children.Add(Theme.Text(label, bold: true));
                text.Children.Add(Theme.Text(hint, gray: true, size: 11.5));
                var box = new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 0, 0, 12), VerticalContentAlignment = VerticalAlignment.Top };
                box.Checked += (s, e) => { set(true); UpdateFeatureVisibility(); Save(); };
                box.Unchecked += (s, e) => { set(false); UpdateFeatureVisibility(); Save(); };
                featureFields[key] = box;
                panel.Children.Add(box);
            }

            Add("interrupts", "Interrupts", "Interrupt support, disabling also disables Timer, Keyboard, Mouse, Network, Scheduler, PCI, Storage",
                props.EnableInterrupts, v => props.EnableInterrupts = v);
            Add("timer", "Timer", "Timers support, disabling also disables Scheduler", props.EnableTimer, v => props.EnableTimer = v);
            Add("keyboard", "Keyboard Support", "Keyboard input handling", props.EnableKeyboard, v => props.EnableKeyboard = v);
            Add("mouse", "Mouse Support", "Mouse input handling", props.EnableMouse, v => props.EnableMouse = v);
            Add("network", "Network Support", "Network stack and drivers", props.EnableNetwork, v => props.EnableNetwork = v);
            Add("scheduler", "Scheduler Support", "Process and thread scheduling", props.EnableScheduler, v => props.EnableScheduler = v);
            Add("pci", "PCI Support", "PCI/PCIe bus enumeration. Every PCI device driver needs it — E1000E, AHCI, NVMe, and VirtIO over PCI. Disabling also disables Storage",
                props.EnablePCI, v => props.EnablePCI = v);
            Add("storage", "Storage Support", "AHCI/SATA and NVMe block devices. Disabling also disables FAT", props.EnableStorage, v => props.EnableStorage = v);
            Add("fat", "FAT Filesystem", "FAT filesystem support, mounted on a storage block device", props.EnableFat, v => props.EnableFat = v);
            Add("audio", "Audio Support", "HD Audio playback over PCI", props.EnableAudio, v => props.EnableAudio = v);
            Add("graphics", "Graphic Support", "Enable graphics display", props.EnableGraphics, v => props.EnableGraphics = v);
            Add("uart", "UART / Serial", "Serial port output. Disabling it silences the serial console the debugger and test runner read",
                props.EnableUART, v => props.EnableUART = v);
            return panel;
        }

        /// <summary>
        /// Mirrors the cascade in Cosmos.Sdk's targets, which is what the build
        /// applies. Graphics and UART are deliberately absent: the SDK never
        /// cascades them, so hiding them would claim a feature was off while the
        /// build kept it on.
        /// </summary>
        private void UpdateFeatureVisibility()
        {
            bool interrupts = props.EnableInterrupts;
            bool timer = interrupts && props.EnableTimer;
            bool pci = interrupts && props.EnablePCI;
            bool storage = pci && props.EnableStorage;
            void Show(string key, bool visible)
            {
                if (featureFields.TryGetValue(key, out FrameworkElement field))
                {
                    field.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            foreach (string key in new[] { "timer", "keyboard", "mouse", "network", "pci" })
            {
                Show(key, interrupts);
            }
            Show("scheduler", timer);
            Show("storage", pci);
            Show("fat", storage);
            Show("audio", pci);
        }

        private FrameworkElement Advanced()
        {
            TextBox flags = TextField(props.CCompilerFlags, v => { props.CCompilerFlags = v; QueueSave(); });
            return Theme.Field("C Compiler Flags", flags, "Replaces the SDK's default flags for the project's C sources; the architecture's target flags are always added. Uses SDK defaults if empty");
        }

        private FrameworkElement Qemu()
        {
            QemuConfig q = props.Qemu;
            bool x64 = props.TargetArch != "arm64";
            var panel = new StackPanel();

            panel.Children.Add(Group("Machine"));
            panel.Children.Add(QemuCombo("Memory", QemuCatalog.Memory, q.Memory, v => q.Memory = v,
                "How much RAM the virtual machine gives your kernel. More lets the kernel allocate more, but uses more host memory."));
            panel.Children.Add(QemuCombo("Machine Type", QemuCatalog.MachineTypes(props.TargetArch), q.MachineType, v => q.MachineType = v,
                "The emulated motherboard/chipset that decides which built-in devices exist. " +
                (x64 ? "Q35 is the modern default; PC is the legacy i440FX." : "arm64 uses the generic ARM virt machine.")));
            panel.Children.Add(QemuCombo("CPU Model", QemuCatalog.CpuModels(props.TargetArch), q.CpuModel, v => q.CpuModel = v,
                "Which processor QEMU emulates for the guest. " +
                (x64 ? "\"Max\" exposes every CPU feature QEMU supports; \"Host\" passes your real CPU through (fastest, needs a hypervisor)."
                     : "Cortex-A72/A53 are common ARM cores; \"Max\" enables all features.")));
            panel.Children.Add(QemuCombo("Serial Output", QemuCatalog.SerialModes, q.SerialMode, v => q.SerialMode = v,
                "Where the kernel's serial console goes — the text from Console.Write / Serial output. \"Standard I/O\" streams it into the Cosmos OS - Output pane."));

            panel.Children.Add(Group("Devices"));
            panel.Children.Add(QemuCombo("Network Card", QemuCatalog.NetworkCards(props.TargetArch), q.NetworkCard, v => q.NetworkCard = v,
                "The network adapter the kernel sees. Pick \"None\" for no networking; cards without a kernel driver are grayed out. Supported: " +
                (x64 ? "Intel E1000E and VirtIO over PCI — VirtIO needs PCI enabled." : "VirtIO over MMIO (virtio-net-device).")));
            TextBox forwards = TextField(string.Join(" ", q.PortForwards), v => { q.PortForwards = ProjectConfig.SplitPortForwards(v); QueueQemuSave(); });
            panel.Children.Add(Theme.Field("Port Forwards", forwards,
                "Host ports forwarded to the guest, separated by spaces, each as [tcp|udp]:[hostaddr]:hostport-[guestaddr]:guestport: " +
                "tcp::2323-:23 reaches the guest's port 23 (Telnet) at localhost:2323. Needs a network card."));
            string inputSupport = x64
                ? "PS/2 (built into the q35 chipset) and VirtIO over PCI — VirtIO needs PCI enabled."
                : "VirtIO over MMIO (the arm64 virt machine has no PS/2).";
            panel.Children.Add(QemuCombo("Keyboard", QemuCatalog.Keyboards(props.TargetArch), q.Keyboard, v => q.Keyboard = v,
                "The keyboard device the kernel reads input from. Devices without a kernel driver are grayed out. Supported: " + inputSupport));
            panel.Children.Add(QemuCombo("Mouse", QemuCatalog.Mice(props.TargetArch), q.Mouse, v => q.Mouse = v,
                "The pointing device the kernel reads. Devices without a kernel driver are grayed out. Supported: " + inputSupport));
            panel.Children.Add(QemuCombo("Audio", QemuCatalog.AudioDevices(props.TargetArch), q.Audio, v => q.Audio = v,
                "The sound card the kernel plays through. Controllers without a kernel driver are grayed out. " +
                (x64 ? "A codec is attached alongside the controller, and the host backend is QEMU's default — audio needs PCI enabled."
                     : "The HD Audio driver has only been run on x64.")));

            panel.Children.Add(Group("Storage"));
            var disks = new StackPanel();
            diskList = new StackPanel();
            RenderDisks();
            disks.Children.Add(diskList);
            Button add = Theme.Button("+ Add Disk", () =>
            {
                props.Qemu.Disks.Add(new DiskConfig { Path = "", Type = "ahci", Size = "256M" });
                RenderDisks();
            });
            add.Margin = new Thickness(0, 6, 0, 0);
            disks.Children.Add(add);
            panel.Children.Add(Theme.Field("Disks", disks,
                "Disk images attached to the kernel at boot. A missing image is created at the given size on launch. Paths are relative to the project folder."));

            panel.Children.Add(Group("Advanced"));
            TextBox extra = TextField(q.ExtraArgs, v => { q.ExtraArgs = v; QueueQemuSave(); });
            panel.Children.Add(Theme.Field("Extra Arguments", extra,
                "Raw flags appended to the QEMU launch command, for advanced options not covered above (e.g. -device …). Leave empty if unsure."));
            return panel;
        }

        private static FrameworkElement Group(string title)
        {
            TextBlock text = Theme.Text(title, bold: true, size: 13);
            text.Margin = new Thickness(0, 6, 0, 10);
            text.Opacity = 0.85;
            return text;
        }

        private void RenderDisks()
        {
            diskList.Children.Clear();
            List<DiskConfig> disks = props.Qemu.Disks;
            if (disks.Count == 0)
            {
                diskList.Children.Add(Theme.Text("No disks attached.", gray: true));
                return;
            }
            foreach (DiskConfig disk in disks.ToList())
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                TextBox path = TextField(disk.Path, v => { disk.Path = v; QueueQemuSave(); });
                path.ToolTip = "Image path (e.g. disk.img)";
                ComboBox type = Combo(QemuCatalog.DiskTypes, disk.Type);
                type.Margin = new Thickness(6, 0, 0, 0);
                type.SelectionChanged += (s, e) => { disk.Type = Selected(type) ?? "ahci"; SaveQemu(); };
                TextBox size = TextField(disk.Size, v => { disk.Size = v; QueueQemuSave(); });
                size.Margin = new Thickness(6, 0, 0, 0);
                size.ToolTip = "Size used when the image is created (e.g. 256M, 1G)";
                Button remove = Theme.Button("✕", () =>
                {
                    props.Qemu.Disks.Remove(disk);
                    RenderDisks();
                    SaveQemu();
                });
                remove.MinWidth = 0;
                remove.Margin = new Thickness(6, 0, 0, 0);
                remove.ToolTip = "Remove disk";

                Grid.SetColumn(type, 1);
                Grid.SetColumn(size, 2);
                Grid.SetColumn(remove, 3);
                row.Children.Add(path);
                row.Children.Add(type);
                row.Children.Add(size);
                row.Children.Add(remove);
                diskList.Children.Add(row);
            }
        }

        private FrameworkElement Packages()
        {
            var panel = new StackPanel();
            if (props.Packages.Count == 0)
            {
                panel.Children.Add(Theme.Text("No additional packages", gray: true));
                return panel;
            }
            foreach (PackageInfo package in props.Packages)
            {
                var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
                TextBlock version = Theme.Text(package.Version, gray: true, wrap: false);
                DockPanel.SetDock(version, Dock.Right);
                row.Children.Add(version);
                row.Children.Add(Theme.Text(package.Name, wrap: false));
                panel.Children.Add(row);
            }
            return panel;
        }

        private FrameworkElement QemuCombo(string label, IReadOnlyList<QemuChoice> choices, string value, Action<string> set, string hint)
        {
            ComboBox combo = Combo(choices, value);
            combo.SelectionChanged += (s, e) =>
            {
                string chosen = Selected(combo);
                if (chosen != null)
                {
                    set(chosen);
                    SaveQemu();
                }
            };
            return Theme.Field(label, combo, hint);
        }

        private static ComboBox Combo(IReadOnlyList<QemuChoice> choices, string value)
        {
            var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (QemuChoice choice in choices)
            {
                combo.Items.Add(new ComboBoxItem { Content = choice.Label, Tag = choice.Value, IsEnabled = choice.Enabled });
            }
            // A hand-edited value outside the list still shows as itself.
            if (!string.IsNullOrEmpty(value) && !choices.Any(c => c.Value == value))
            {
                combo.Items.Add(new ComboBoxItem { Content = value, Tag = value });
            }
            combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == value && i.IsEnabled)
                ?? combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => i.IsEnabled);
            return combo;
        }

        private static string Selected(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag as string;

        private static TextBox TextField(string value, Action<string> changed)
        {
            var box = new TextBox { Text = value ?? "" };
            box.TextChanged += (s, e) => changed(box.Text);
            return box;
        }

        private void QueueSave()
        {
            saveTimer.Stop();
            saveTimer.Start();
        }

        private void QueueQemuSave()
        {
            qemuSaveTimer.Stop();
            qemuSaveTimer.Start();
        }

        private void Save()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            saveTimer.Stop();
            try
            {
                ProjectConfig.Save(project.Csproj, props);
                ShowSaved();
                CosmosPackage.Instance?.Workspace.Refresh();
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                CosmosPackage.Instance?.ShowMessage("Failed to save: " + e.Message, OLEMSGICON.OLEMSGICON_CRITICAL);
            }
        }

        private void SaveQemu()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            qemuSaveTimer.Stop();
            try
            {
                ProjectConfig.SaveQemuConfig(project.ProjectDir, props.Qemu);
                ShowSaved();
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                CosmosPackage.Instance?.ShowMessage("Failed to save QEMU config: " + e.Message, OLEMSGICON.OLEMSGICON_CRITICAL);
            }
        }

        private void ShowSaved()
        {
            savedText.Visibility = Visibility.Visible;
            savedTimer.Stop();
            savedTimer.Start();
        }

        private void OpenCsproj()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            VsShellUtilities.OpenDocument(CosmosPackage.Instance, project.Csproj);
        }
    }
}
