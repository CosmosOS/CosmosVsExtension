using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Cosmos.VisualStudio.Core
{
    public sealed class DiskConfig
    {
        // Image path, absolute or relative to the project directory.
        public string Path { get; set; } = "";
        // Controller the guest sees the disk through: "ahci" or "nvme".
        public string Type { get; set; } = "ahci";
        // Size used only when the image has to be created (e.g. "256M", "1G").
        public string Size { get; set; } = "256M";

        public DiskConfig Clone() => new DiskConfig { Path = Path, Type = Type, Size = Size };
    }

    public sealed class QemuConfig
    {
        public string Memory { get; set; } = "512M";
        public string MachineType { get; set; } = "q35";
        public string CpuModel { get; set; } = "max";
        public bool EnableNetwork { get; set; }
        public string NetworkPorts { get; set; } = "5555";
        public string SerialMode { get; set; } = "stdio";
        // QEMU NIC model exposed to the guest, or "none" for no network card.
        public string NetworkCard { get; set; } = "none";
        // Host ports forwarded to the guest, each a QEMU hostfwd rule such as
        // "tcp::2323-:23" (the guest's port 23 at localhost:2323). They ride on
        // the network card's user-mode backend, so they need a card.
        public List<string> PortForwards { get; set; } = new List<string>();
        // "ps2" (x64 chipset), "virtio-keyboard-device" (arm64), or "none".
        public string Keyboard { get; set; } = "ps2";
        public string Mouse { get; set; } = "ps2";
        // A QEMU HD Audio controller or "none". The codec riding on it is the
        // launcher's business, not a separate choice here.
        public string Audio { get; set; } = "none";
        public string ExtraArgs { get; set; } = "";
        // Disk images attached to the kernel at boot.
        public List<DiskConfig> Disks { get; set; } = new List<DiskConfig>();
    }

    public sealed class PackageInfo
    {
        public PackageInfo(string name, string version)
        {
            Name = name;
            Version = version;
        }

        public string Name { get; }
        public string Version { get; }
    }

    public sealed class ProjectProperties
    {
        public string Name { get; set; } = "";
        public string TargetFramework { get; set; } = "net10.0";
        public string TargetArch { get; set; } = "x64";
        public string KernelClass { get; set; } = "";
        public bool EnableInterrupts { get; set; } = true;
        public bool EnableTimer { get; set; } = true;
        public bool EnableGraphics { get; set; } = true;
        public bool EnableKeyboard { get; set; } = true;
        public bool EnableMouse { get; set; } = true;
        public bool EnableNetwork { get; set; } = true;
        public bool EnableScheduler { get; set; } = true;
        public bool EnableUART { get; set; } = true;
        public bool EnablePCI { get; set; } = true;
        public bool EnableStorage { get; set; } = true;
        public bool EnableFat { get; set; } = true;
        public bool EnableAudio { get; set; } = true;
        // CCCompilerFlags: replaces the SDK's default C flags; the target flags are always added.
        public string CCompilerFlags { get; set; } = "";
        public List<PackageInfo> Packages { get; set; } = new List<PackageInfo>();
        public QemuConfig Qemu { get; set; } = new QemuConfig();
    }

    /// <summary>One choice of a QEMU device dropdown. Disabled choices have no kernel driver.</summary>
    public sealed class QemuChoice
    {
        public QemuChoice(string value, string label, bool enabled = true)
        {
            Value = value;
            Label = label;
            Enabled = enabled;
        }

        public string Value { get; }
        public string Label { get; }
        public bool Enabled { get; }
    }

    /// <summary>
    /// The QEMU devices each architecture offers. The properties page renders
    /// these and <see cref="ProjectConfig.LoadQemuConfig"/> resets anything not
    /// enabled here, so the two never disagree.
    /// </summary>
    public static class QemuCatalog
    {
        public static IReadOnlyList<QemuChoice> Memory { get; } = new[]
        {
            new QemuChoice("256M", "256 MB"),
            new QemuChoice("512M", "512 MB"),
            new QemuChoice("1G", "1 GB"),
            new QemuChoice("2G", "2 GB"),
            new QemuChoice("4G", "4 GB")
        };

        public static IReadOnlyList<QemuChoice> SerialModes { get; } = new[]
        {
            new QemuChoice("stdio", "Standard I/O (Output window)"),
            new QemuChoice("none", "Disabled")
        };

        public static IReadOnlyList<QemuChoice> DiskTypes { get; } = new[]
        {
            new QemuChoice("ahci", "AHCI"),
            new QemuChoice("nvme", "NVMe")
        };

        public static IReadOnlyList<QemuChoice> MachineTypes(string arch) => arch == "arm64"
            ? new[] { new QemuChoice("virt", "Virt (ARM Virtual Machine)") }
            : new[] { new QemuChoice("q35", "Q35 (Modern chipset)"), new QemuChoice("pc", "PC (Legacy i440FX)") };

        public static IReadOnlyList<QemuChoice> CpuModels(string arch) => arch == "arm64"
            ? new[] { new QemuChoice("cortex-a72", "Cortex-A72"), new QemuChoice("cortex-a53", "Cortex-A53"), new QemuChoice("max", "Max (All features)") }
            : new[] { new QemuChoice("max", "Max (All features)"), new QemuChoice("qemu64", "QEMU64 (Basic)"), new QemuChoice("host", "Host (Pass-through)") };

        // The virtio drivers are transport-agnostic: one VirtioNet binds over
        // virtio-mmio on the arm64 virt machine and over virtio-pci on q35, which
        // has no virtio-mmio window. The model is *-pci on x64 and *-device on
        // arm64 because those are genuinely different QEMU devices.
        public static IReadOnlyList<QemuChoice> NetworkCards(string arch) => arch == "arm64"
            ? new[]
            {
                new QemuChoice("none", "None (no network card)"),
                new QemuChoice("virtio-net-device", "VirtIO (virtio-net-device)"),
                new QemuChoice("e1000e", "Intel E1000E — x64 only", false)
            }
            : new[]
            {
                new QemuChoice("none", "None (no network card)"),
                new QemuChoice("e1000e", "Intel E1000E (PCIe)"),
                new QemuChoice("virtio-net-pci", "VirtIO (virtio-net-pci)"),
                new QemuChoice("e1000", "Intel E1000 — no driver", false),
                new QemuChoice("rtl8139", "Realtek RTL8139 — no driver", false)
            };

        // x64 has PS/2 (i8042) built into q35, plus virtio-input over PCI; the
        // arm64 virt machine has no PS/2 controller and uses virtio-input over MMIO.
        public static IReadOnlyList<QemuChoice> Keyboards(string arch) => arch == "arm64"
            ? new[]
            {
                new QemuChoice("none", "None (no keyboard)"),
                new QemuChoice("virtio-keyboard-device", "VirtIO Keyboard (MMIO)"),
                new QemuChoice("ps2", "PS/2 — virt has no i8042", false)
            }
            : new[]
            {
                new QemuChoice("none", "None (no keyboard)"),
                new QemuChoice("ps2", "PS/2 (i8042)"),
                new QemuChoice("virtio-keyboard-pci", "VirtIO Keyboard (PCI)"),
                new QemuChoice("virtio-keyboard-device", "VirtIO Keyboard (MMIO) — arm64 only", false)
            };

        public static IReadOnlyList<QemuChoice> Mice(string arch) => arch == "arm64"
            ? new[]
            {
                new QemuChoice("none", "None (no mouse)"),
                new QemuChoice("virtio-mouse-device", "VirtIO Mouse (MMIO)"),
                new QemuChoice("ps2", "PS/2 — virt has no i8042", false)
            }
            : new[]
            {
                new QemuChoice("none", "None (no mouse)"),
                new QemuChoice("ps2", "PS/2 (i8042)"),
                new QemuChoice("virtio-mouse-pci", "VirtIO Mouse (PCI)"),
                new QemuChoice("virtio-mouse-device", "VirtIO Mouse (MMIO) — arm64 only", false)
            };

        // The HD Audio driver binds over PCI, which the arm64 virt machine also
        // has, but it has only been run on x64 — arm64 stays at "none" until that
        // is more than a guess.
        public static IReadOnlyList<QemuChoice> AudioDevices(string arch) => arch == "arm64"
            ? new[]
            {
                new QemuChoice("none", "None (no audio)"),
                new QemuChoice("intel-hda", "Intel HD Audio — x64 only", false)
            }
            : new[]
            {
                new QemuChoice("none", "None (no audio)"),
                new QemuChoice("intel-hda", "Intel HD Audio (ICH6)"),
                new QemuChoice("ich9-intel-hda", "Intel HD Audio (ICH9)"),
                new QemuChoice("ac97", "Intel AC97 — no driver", false),
                new QemuChoice("es1370", "ENSONIQ ES1370 — no driver", false)
            };

        public static bool IsValid(IEnumerable<QemuChoice> choices, string value) =>
            choices.Any(c => c.Enabled && c.Value == value);
    }

    /// <summary>Reads and writes the kernel csproj properties and <c>.cosmos/config.json</c>.</summary>
    public static class ProjectConfig
    {
        public static QemuConfig DefaultQemuConfig(string arch)
        {
            bool arm = arch == "arm64";
            return new QemuConfig
            {
                Memory = "512M",
                MachineType = arm ? "virt" : "q35",
                CpuModel = arm ? "cortex-a72" : "max",
                EnableNetwork = false,
                NetworkPorts = "5555",
                SerialMode = "stdio",
                NetworkCard = "none",
                PortForwards = new List<string>(),
                // x64 gets PS/2 from the chipset; arm64 virt needs virtio-input devices.
                Keyboard = arm ? "virtio-keyboard-device" : "ps2",
                Mouse = arm ? "virtio-mouse-device" : "ps2",
                // Off by default: audio is something a kernel opts into.
                Audio = "none",
                ExtraArgs = "",
                Disks = new List<DiskConfig>()
            };
        }

        public static JObject QemuToJson(QemuConfig q)
        {
            return new JObject
            {
                ["memory"] = q.Memory,
                ["machineType"] = q.MachineType,
                ["cpuModel"] = q.CpuModel,
                ["enableNetwork"] = q.EnableNetwork,
                ["networkPorts"] = q.NetworkPorts,
                ["serialMode"] = q.SerialMode,
                ["networkCard"] = q.NetworkCard,
                ["portForwards"] = new JArray(q.PortForwards.Cast<object>().ToArray()),
                ["keyboard"] = q.Keyboard,
                ["mouse"] = q.Mouse,
                ["audio"] = q.Audio,
                ["extraArgs"] = q.ExtraArgs,
                ["disks"] = DisksToJson(q.Disks)
            };
        }

        public static JArray DisksToJson(IEnumerable<DiskConfig> disks) =>
            new JArray(disks.Select(d => new JObject
            {
                ["path"] = d.Path,
                ["type"] = d.Type,
                ["size"] = d.Size
            }).Cast<object>().ToArray());

        public static QemuConfig LoadQemuConfig(string projectDir, string arch)
        {
            QemuConfig defaults = DefaultQemuConfig(arch);
            if (!(CosmosProject.ReadConfig(projectDir)?["qemu"] is JObject saved))
            {
                return defaults;
            }

            var q = new QemuConfig
            {
                Memory = StringOr(saved["memory"], defaults.Memory),
                MachineType = StringOr(saved["machineType"], defaults.MachineType),
                CpuModel = StringOr(saved["cpuModel"], defaults.CpuModel),
                EnableNetwork = saved["enableNetwork"]?.Type == JTokenType.Boolean ? (bool)saved["enableNetwork"] : defaults.EnableNetwork,
                NetworkPorts = StringOr(saved["networkPorts"], defaults.NetworkPorts),
                SerialMode = StringOr(saved["serialMode"], defaults.SerialMode),
                NetworkCard = StringOr(saved["networkCard"], defaults.NetworkCard),
                PortForwards = NormalizePortForwards(saved["portForwards"]),
                Keyboard = StringOr(saved["keyboard"], defaults.Keyboard),
                Mouse = StringOr(saved["mouse"], defaults.Mouse),
                Audio = StringOr(saved["audio"], defaults.Audio),
                ExtraArgs = StringOr(saved["extraArgs"], defaults.ExtraArgs),
                // Disks may be absent or malformed in older configs; normalize to
                // a clean list so consumers never have to defend against it.
                Disks = (saved["disks"] as JArray ?? new JArray())
                    .OfType<JObject>()
                    .Where(d => d["path"]?.Type == JTokenType.String)
                    .Select(d => new DiskConfig
                    {
                        Path = (string)d["path"],
                        Type = (string)d["type"] == "nvme" ? "nvme" : "ahci",
                        Size = d["size"]?.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)d["size"]) ? (string)d["size"] : "256M"
                    })
                    .ToList()
            };

            // Anything the architecture has no driver for is reset to the default
            // rather than handed to QEMU.
            if (!QemuCatalog.IsValid(QemuCatalog.MachineTypes(arch), q.MachineType)) q.MachineType = defaults.MachineType;
            if (!QemuCatalog.IsValid(QemuCatalog.CpuModels(arch), q.CpuModel)) q.CpuModel = defaults.CpuModel;
            if (!QemuCatalog.IsValid(QemuCatalog.NetworkCards(arch), q.NetworkCard)) q.NetworkCard = defaults.NetworkCard;
            if (!QemuCatalog.IsValid(QemuCatalog.Keyboards(arch), q.Keyboard)) q.Keyboard = defaults.Keyboard;
            if (!QemuCatalog.IsValid(QemuCatalog.Mice(arch), q.Mouse)) q.Mouse = defaults.Mouse;
            if (!QemuCatalog.IsValid(QemuCatalog.AudioDevices(arch), q.Audio)) q.Audio = defaults.Audio;
            return q;
        }

        private static string StringOr(JToken token, string fallback) =>
            token != null && token.Type == JTokenType.String ? (string)token : fallback;

        /// <summary>
        /// Port forwards may be absent in older configs, or hand-written as one
        /// string; normalize to a clean list of rules. A string is split on
        /// spaces and commas, which no hostfwd rule contains.
        /// </summary>
        public static List<string> NormalizePortForwards(JToken value)
        {
            IEnumerable<string> items;
            if (value != null && value.Type == JTokenType.String)
            {
                items = SplitPortForwards((string)value);
            }
            else if (value is JArray array)
            {
                items = array.Where(t => t.Type == JTokenType.String).Select(t => (string)t);
            }
            else
            {
                items = Enumerable.Empty<string>();
            }
            return items.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).ToList();
        }

        public static List<string> SplitPortForwards(string text) =>
            Regex.Split(text ?? "", @"[\s,]+").Where(r => r.Length > 0).ToList();

        public static void SaveQemuConfig(string projectDir, QemuConfig qemu)
        {
            CosmosProject.UpdateConfig(projectDir, config =>
            {
                // Keep keys this version doesn't know about.
                var merged = config["qemu"] as JObject ?? new JObject();
                foreach (JProperty p in QemuToJson(qemu).Properties())
                {
                    merged[p.Name] = p.Value;
                }
                config["qemu"] = merged;
            });
        }

        public static ProjectProperties Parse(string csproj)
        {
            string content = File.ReadAllText(csproj);
            string name = Path.GetFileNameWithoutExtension(csproj);
            string projectDir = Path.GetDirectoryName(csproj);
            string targetArch = CosmosProject.ReadTargetArch(projectDir);

            var packages = new List<PackageInfo>();
            foreach (Match m in Regex.Matches(content, "<PackageReference\\s+Include=\"([^\"]+)\"\\s+Version=\"([^\"]+)\""))
            {
                string id = m.Groups[1].Value;
                if (!id.StartsWith("Cosmos.Build.", StringComparison.Ordinal) && !id.StartsWith("Cosmos.Kernel.Native", StringComparison.Ordinal))
                {
                    packages.Add(new PackageInfo(id, m.Groups[2].Value));
                }
            }

            // Features are on unless the csproj turns them off explicitly.
            bool Enabled(string prop) => GetProperty(content, prop) != "false";

            string kernelClass = GetProperty(content, "CosmosKernelClass");
            return new ProjectProperties
            {
                Name = name,
                TargetFramework = NonEmpty(GetProperty(content, "TargetFramework"), "net10.0"),
                TargetArch = targetArch,
                KernelClass = NonEmpty(kernelClass, name + ".Kernel"),
                EnableInterrupts = Enabled("CosmosEnableInterrupts"),
                EnableTimer = Enabled("CosmosEnableTimer"),
                EnableGraphics = Enabled("CosmosEnableGraphics"),
                EnableKeyboard = Enabled("CosmosEnableKeyboard"),
                EnableMouse = Enabled("CosmosEnableMouse"),
                EnableNetwork = Enabled("CosmosEnableNetwork"),
                EnableScheduler = Enabled("CosmosEnableScheduler"),
                EnableUART = Enabled("CosmosEnableUART"),
                EnablePCI = Enabled("CosmosEnablePCI"),
                EnableStorage = Enabled("CosmosEnableStorage"),
                EnableFat = Enabled("CosmosEnableFat"),
                EnableAudio = Enabled("CosmosEnableAudio"),
                CCompilerFlags = GetProperty(content, "CCCompilerFlags"),
                Packages = packages,
                Qemu = LoadQemuConfig(projectDir, targetArch)
            };
        }

        private static string NonEmpty(string value, string fallback) => string.IsNullOrEmpty(value) ? fallback : value;

        public static string GetProperty(string content, string prop)
        {
            Match m = Regex.Match(content, "<" + prop + ">([^<]*)</" + prop + ">");
            return m.Success ? UnescapeXml(m.Groups[1].Value) : "";
        }

        public static void Save(string csproj, ProjectProperties props)
        {
            string content = File.ReadAllText(csproj);

            content = SetProperty(content, "TargetFramework", NonEmpty(props.TargetFramework, "net10.0"));

            CosmosProject.UpdateConfig(Path.GetDirectoryName(csproj), config =>
                config["targetArch"] = NonEmpty(props.TargetArch, "x64"));

            if (!string.IsNullOrEmpty(props.KernelClass))
            {
                content = SetProperty(content, "CosmosKernelClass", props.KernelClass);
            }

            content = string.IsNullOrEmpty(props.CCompilerFlags)
                ? RemoveProperty(content, "CCCompilerFlags")
                : SetProperty(content, "CCCompilerFlags", props.CCompilerFlags);

            // On is the SDK default, so only an off switch is written.
            content = SetFeature(content, "CosmosEnableInterrupts", props.EnableInterrupts);
            content = SetFeature(content, "CosmosEnableTimer", props.EnableTimer);
            content = SetFeature(content, "CosmosEnableGraphics", props.EnableGraphics);
            content = SetFeature(content, "CosmosEnableKeyboard", props.EnableKeyboard);
            content = SetFeature(content, "CosmosEnableMouse", props.EnableMouse);
            content = SetFeature(content, "CosmosEnableNetwork", props.EnableNetwork);
            content = SetFeature(content, "CosmosEnableScheduler", props.EnableScheduler);
            content = SetFeature(content, "CosmosEnableUART", props.EnableUART);
            content = SetFeature(content, "CosmosEnablePCI", props.EnablePCI);
            content = SetFeature(content, "CosmosEnableStorage", props.EnableStorage);
            content = SetFeature(content, "CosmosEnableFat", props.EnableFat);
            content = SetFeature(content, "CosmosEnableAudio", props.EnableAudio);

            File.WriteAllText(csproj, content);
        }

        private static string SetFeature(string content, string prop, bool enabled) =>
            enabled ? RemoveProperty(content, prop) : SetProperty(content, prop, "false");

        /// <summary>Replaces the property's value, or adds it to the first PropertyGroup.</summary>
        public static string SetProperty(string content, string prop, string value)
        {
            string element = "<" + prop + ">" + EscapeXml(value) + "</" + prop + ">";
            var existing = new Regex("<" + prop + ">[^<]*</" + prop + ">");
            if (existing.IsMatch(content))
            {
                return existing.Replace(content, _ => element, 1);
            }
            Match group = Regex.Match(content, "<PropertyGroup[^>]*>");
            if (!group.Success)
            {
                return content;
            }
            string newline = content.Contains("\r\n") ? "\r\n" : "\n";
            return content.Insert(group.Index + group.Length, newline + "    " + element);
        }

        /// <summary>Removes every occurrence of the property, along with the line it sat on.</summary>
        public static string RemoveProperty(string content, string prop)
        {
            content = Regex.Replace(content, "\\r?\\n[ \\t]*<" + prop + ">[^<]*</" + prop + ">[ \\t]*(?=\\r?\\n)", "");
            return Regex.Replace(content, "<" + prop + ">[^<]*</" + prop + ">", "");
        }

        private static string EscapeXml(string value) =>
            value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

        private static string UnescapeXml(string value) =>
            value.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&apos;", "'").Replace("&amp;", "&");
    }
}
