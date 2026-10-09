using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Build.Framework.XamlTypes;

namespace Cosmos.VisualStudio.Core
{
    /// <summary>
    /// The Cosmos and QEMU pages of Visual Studio's Project Properties, as the
    /// <see cref="Rule"/> objects the project system builds its UI from (see
    /// https://github.com/dotnet/project-system/tree/main/docs/repo/property-pages).
    /// Kernel switches are MSBuild properties of the csproj; the architecture
    /// and the QEMU settings live in <c>.cosmos/config.json</c> and go through
    /// the <see cref="ConfigPersistence"/> property provider.
    /// </summary>
    public static class KernelPropertyPages
    {
        /// <summary>The project capability of a Cosmos kernel: a project that uses Cosmos.Sdk.</summary>
        public const string Capability = "CosmosKernel";

        /// <summary>The data source persistence of the settings stored in <c>.cosmos/config.json</c>.</summary>
        public const string ConfigPersistence = "CosmosKernelConfig";

        public const string KernelPage = "CosmosKernel";
        public const string QemuPage = "CosmosQemu";

        public static IReadOnlyList<Rule> CreateRules() => new[] { KernelRule(), QemuRule() };

        private static Rule KernelRule()
        {
            var rule = NewRule(KernelPage, "Cosmos", "Cosmos kernel settings: target architecture, entry class and kernel features.", 10,
                new DataSource { Persistence = "ProjectFile", HasConfigurationCondition = false, SourceOfDefaultValue = DefaultValueSourceLocation.AfterContext },
                ("General", "General"), ("Features", "Features"), ("Advanced", "Advanced"));

            rule.Properties.Add(ConfigEnum(KernelConfigSettings.TargetArch, "Target architecture",
                "The architecture the kernel is built for and runs on in QEMU. The QEMU page lists the devices of the architecture chosen here.",
                "General", null));
            rule.Properties.Add(new StringProperty
            {
                Name = "CosmosKernelClass",
                DisplayName = "Kernel entry class",
                Description = "Fully qualified name of the kernel class (e.g. MyKernel.Kernel). Defaults to the root namespace followed by .Kernel.",
                Category = "General"
            });

            const string Interrupts = "(has-evaluated-value \"" + KernelPage + "\" \"CosmosEnableInterrupts\" true)";
            const string Timer = "(has-evaluated-value \"" + KernelPage + "\" \"CosmosEnableTimer\" true)";
            const string Pci = "(has-evaluated-value \"" + KernelPage + "\" \"CosmosEnablePCI\" true)";
            const string Storage = "(has-evaluated-value \"" + KernelPage + "\" \"CosmosEnableStorage\" true)";
            // The visibility mirrors the cascade in Cosmos.Sdk's targets, which
            // turns a feature off with the one it needs; the evaluated values
            // already carry it. Graphics and UART never cascade. USB and audio
            // stay hidden under an SDK that has no such switch.
            AddFeature(rule, "CosmosEnableInterrupts", "Interrupts",
                "Interrupt support. Disabling it also disables Timer, Keyboard, Mouse, Network, Scheduler, PCI and Storage.", null);
            AddFeature(rule, "CosmosEnableTimer", "Timer", "Timer support. Disabling it also disables Scheduler.", Interrupts);
            AddFeature(rule, "CosmosEnableKeyboard", "Keyboard", "Keyboard input handling.", Interrupts);
            AddFeature(rule, "CosmosEnableMouse", "Mouse", "Mouse input handling.", Interrupts);
            AddFeature(rule, "CosmosEnableNetwork", "Network", "Network stack and drivers.", Interrupts);
            AddFeature(rule, "CosmosEnableScheduler", "Scheduler", "Process and thread scheduling.", Timer);
            AddFeature(rule, "CosmosEnablePCI", "PCI",
                "PCI/PCIe bus enumeration. Every PCI device driver needs it: E1000E, AHCI, NVMe, xHCI, HD Audio and VirtIO over PCI. Disabling it also disables Storage, USB and Audio.",
                Interrupts);
            AddFeature(rule, "CosmosEnableStorage", "Storage", "AHCI/SATA, NVMe and VirtIO block devices. Disabling it also disables FAT.", Pci);
            AddFeature(rule, "CosmosEnableFat", "FAT filesystem", "FAT filesystem support, mounted on a storage block device.", Storage);
            AddFeature(rule, "CosmosEnableUsb", "USB",
                "The xHCI host controller with the hub, HID keyboard and mass storage drivers. On by default when Keyboard, Mouse or Storage is.",
                And(Pci, Defined("CosmosEnableUsb")));
            AddFeature(rule, "CosmosEnableAudio", "Audio", "HD Audio playback over PCI.", And(Pci, Defined("CosmosEnableAudio")));
            AddFeature(rule, "CosmosEnableGraphics", "Graphics", "Graphics display. Without it the kernel runs headless in QEMU.", null);
            AddFeature(rule, "CosmosEnableUART", "UART / serial",
                "Serial port output. Disabling it silences the serial console the debugger and the test runner read.", null);

            rule.Properties.Add(new StringProperty
            {
                Name = "CCCompilerFlags",
                DisplayName = "C compiler flags",
                Description = "Flags for the project's C sources. They replace the SDK's defaults (-O2 -fno-stack-protector -nostdinc -fno-builtin); the architecture's target flags are always added. Leave empty for the defaults.",
                Category = "Advanced"
            });
            rule.EndInit();
            return rule;
        }

        private static Rule QemuRule()
        {
            var rule = NewRule(QemuPage, "QEMU", "The virtual machine the kernel boots in: machine, devices and disks.", 20,
                new DataSource { Persistence = ConfigPersistence, HasConfigurationCondition = false },
                ("Machine", "Machine"), ("Devices", "Devices"), ("Storage", "Storage"), ("Advanced", "Advanced"));

            rule.Properties.Add(ConfigEnum("QemuMemory", "Memory",
                "How much RAM the virtual machine gives the kernel. More lets the kernel allocate more, but uses more host memory.", "Machine", null));
            AddPerArch(rule, "QemuMachineType", "Machine type", "Machine", arch => arch == "arm64"
                ? "The emulated board, which decides the built-in devices. arm64 uses the generic ARM virt machine."
                : "The emulated chipset, which decides the built-in devices. Q35 is the modern default; PC is the legacy i440FX.");
            AddPerArch(rule, "QemuCpuModel", "CPU model", "Machine", arch => arch == "arm64"
                ? "The processor QEMU emulates. Cortex-A72 and Cortex-A53 are common ARM cores; Max enables every feature."
                : "The processor QEMU emulates. Max exposes every CPU feature QEMU supports; Host passes the real CPU through (fastest, needs a hypervisor).");
            rule.Properties.Add(ConfigEnum("QemuSerialMode", "Serial output",
                "Where the kernel's serial console goes. Standard I/O streams it into the Cosmos OS - Output pane.", "Machine", null));

            string InputSupport(string arch) => arch == "arm64"
                ? "VirtIO over MMIO (the arm64 virt machine has no PS/2)."
                : "PS/2 (built into the chipset) and VirtIO over PCI, which needs PCI enabled.";
            AddPerArch(rule, "QemuNetworkCard", "Network card", "Devices", arch =>
                "The network adapter the kernel sees, or None for no networking. Only cards with a kernel driver are listed: " +
                (arch == "arm64" ? "VirtIO over MMIO." : "Intel E1000E and VirtIO over PCI, which needs PCI enabled."));
            rule.Properties.Add(new StringProperty
            {
                Name = "QemuPortForwards",
                DisplayName = "Port forwards",
                Description = "Host ports forwarded to the guest, separated by spaces, each as [tcp|udp]:[hostaddr]:hostport-[guestaddr]:guestport. " +
                              "tcp::2323-:23 reaches the guest's port 23 (Telnet) at localhost:2323. Needs a network card.",
                Category = "Devices"
            });
            AddPerArch(rule, "QemuKeyboard", "Keyboard", "Devices", arch =>
                "The keyboard the kernel reads input from. Only devices with a kernel driver are listed: " + InputSupport(arch));
            AddPerArch(rule, "QemuMouse", "Mouse", "Devices", arch =>
                "The pointing device the kernel reads. Only devices with a kernel driver are listed: " + InputSupport(arch));
            AddPerArch(rule, "QemuAudio", "Audio", "Devices", arch => arch == "arm64"
                ? "The sound card the kernel plays through. The HD Audio driver has only been run on x64."
                : "The sound card the kernel plays through. A codec is attached alongside the controller; audio needs PCI enabled.");

            var disks = new StringProperty
            {
                Name = "QemuDisks",
                DisplayName = "Disks",
                Description = "Disk images attached at boot, one per line: the image path, relative to the project folder, then nvme for an NVMe " +
                              "controller instead of AHCI and the size to create a missing image with (256M unless given). For example: data.img nvme 1G",
                Category = "Storage"
            };
            disks.ValueEditors.Add(Editor("MultiLineString", ("UseMonospaceFont", "True")));
            rule.Properties.Add(disks);

            rule.Properties.Add(new StringProperty
            {
                Name = "QemuExtraArgs",
                DisplayName = "Extra arguments",
                Description = "Raw flags appended to the QEMU command line, for options not covered above (e.g. -device ...). Leave empty if unsure.",
                Category = "Advanced"
            });
            rule.EndInit();
            return rule;
        }

        private static Rule NewRule(string name, string displayName, string description, int order, DataSource dataSource,
            params (string Name, string DisplayName)[] categories)
        {
            var rule = new Rule
            {
                Name = name,
                DisplayName = displayName,
                Description = description,
                PageTemplate = "generic",
                Order = order,
                DataSource = dataSource
            };
            rule.BeginInit();
            foreach ((string categoryName, string categoryDisplayName) in categories)
            {
                rule.Categories.Add(new Category { Name = categoryName, DisplayName = categoryDisplayName });
            }
            return rule;
        }

        private static void AddFeature(Rule rule, string name, string displayName, string description, string visibleWhen)
        {
            var property = new BoolProperty { Name = name, DisplayName = displayName, Description = description, Category = "Features" };
            SetVisibility(property, visibleWhen);
            rule.Properties.Add(property);
        }

        /// <summary>One property per architecture, each shown only when the project targets it.</summary>
        private static void AddPerArch(Rule rule, string name, string displayName, string category, Func<string, string> description)
        {
            foreach (QemuChoice arch in KernelConfigSettings.Architectures)
            {
                string archIs = "(eq (unevaluated \"" + KernelPage + "\" \"" + KernelConfigSettings.TargetArch + "\") \"arm64\")";
                rule.Properties.Add(ConfigEnum(KernelConfigSettings.PerArchName(name, arch.Value), displayName, description(arch.Value), category,
                    arch.Value == "arm64" ? archIs : "(not " + archIs + ")"));
            }
        }

        /// <summary>A dropdown over a config.json setting, listing the choices that have a kernel driver.</summary>
        private static EnumProperty ConfigEnum(string name, string displayName, string description, string category, string visibleWhen)
        {
            KernelConfigSetting setting = KernelConfigSettings.Find(name);
            var property = new EnumProperty
            {
                Name = name,
                DisplayName = displayName,
                Description = description,
                Category = category,
                DataSource = new DataSource { Persistence = ConfigPersistence, HasConfigurationCondition = false }
            };
            foreach (QemuChoice choice in setting.Choices(setting.Arch ?? "x64").Where(c => c.Enabled))
            {
                property.AdmissibleValues.Add(new EnumValue { Name = choice.Value, DisplayName = choice.Label });
            }
            SetVisibility(property, visibleWhen);
            return property;
        }

        private static void SetVisibility(BaseProperty property, string visibleWhen)
        {
            if (visibleWhen != null)
            {
                property.Metadata.Add(new NameValuePair { Name = "VisibilityCondition", Value = visibleWhen });
            }
        }

        private static string And(params string[] conditions) => "(and " + string.Join(" ", conditions) + ")";

        // An SDK that doesn't know the switch evaluates it to nothing.
        private static string Defined(string property) => "(not (has-evaluated-value \"" + KernelPage + "\" \"" + property + "\" \"\"))";

        private static ValueEditor Editor(string type, params (string Name, string Value)[] metadata)
        {
            var editor = new ValueEditor { EditorType = type };
            foreach ((string metadataName, string value) in metadata)
            {
                editor.Metadata.Add(new NameValuePair { Name = metadataName, Value = value });
            }
            return editor;
        }
    }
}
