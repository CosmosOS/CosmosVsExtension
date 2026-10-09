using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace Cosmos.VisualStudio.Core
{
    /// <summary>
    /// One setting of <c>.cosmos/config.json</c> as the Project Properties pages
    /// show it: a property name, the JSON key it lives under and how its value
    /// reads back.
    /// </summary>
    public sealed class KernelConfigSetting
    {
        internal KernelConfigSetting(string name, string key, bool qemu, string arch, Func<string, IReadOnlyList<QemuChoice>> choices,
            Func<QemuConfig, string> read, Func<string, JToken> toJson)
        {
            Name = name;
            Key = key;
            IsQemu = qemu;
            Arch = arch;
            Choices = choices;
            Read = read;
            ToJson = toJson;
        }

        /// <summary>The property name on the page.</summary>
        public string Name { get; }

        /// <summary>The JSON key, under the <c>qemu</c> object when <see cref="IsQemu"/>.</summary>
        public string Key { get; }

        public bool IsQemu { get; }

        /// <summary>
        /// The architecture whose choices the setting lists, or null when both
        /// share it. The page shows the variant of the project's architecture;
        /// both variants read and write the same key.
        /// </summary>
        public string Arch { get; }

        /// <summary>The choices for an architecture, or null for free text.</summary>
        public Func<string, IReadOnlyList<QemuChoice>> Choices { get; }

        internal Func<QemuConfig, string> Read { get; }
        internal Func<string, JToken> ToJson { get; }
    }

    /// <summary>
    /// Reads and writes the <c>.cosmos/config.json</c> settings behind the
    /// Project Properties pages, one property at a time. Values read back
    /// normalized the way <see cref="ProjectConfig.LoadQemuConfig"/> hands them
    /// to the launcher, so a page shows what the kernel will boot with.
    /// </summary>
    public static class KernelConfigSettings
    {
        public const string TargetArch = "TargetArch";

        private static readonly object WriteLock = new object();

        public static IReadOnlyList<QemuChoice> Architectures { get; } = new[]
        {
            new QemuChoice("x64", "x64 (Intel/AMD 64-bit)"),
            new QemuChoice("arm64", "ARM64")
        };

        public static IReadOnlyList<KernelConfigSetting> All { get; } = CreateAll();

        private static IReadOnlyList<KernelConfigSetting> CreateAll()
        {
            var all = new List<KernelConfigSetting>
            {
                new KernelConfigSetting(TargetArch, "targetArch", false, null, _ => Architectures, null, v => v),
                Qemu("QemuMemory", "memory", q => q.Memory, _ => QemuCatalog.Memory),
                Qemu("QemuSerialMode", "serialMode", q => q.SerialMode, _ => QemuCatalog.SerialModes)
            };
            PerArch(all, "QemuMachineType", "machineType", QemuCatalog.MachineTypes, q => q.MachineType);
            PerArch(all, "QemuCpuModel", "cpuModel", QemuCatalog.CpuModels, q => q.CpuModel);
            PerArch(all, "QemuNetworkCard", "networkCard", QemuCatalog.NetworkCards, q => q.NetworkCard);
            PerArch(all, "QemuKeyboard", "keyboard", QemuCatalog.Keyboards, q => q.Keyboard);
            PerArch(all, "QemuMouse", "mouse", QemuCatalog.Mice, q => q.Mouse);
            PerArch(all, "QemuAudio", "audio", QemuCatalog.AudioDevices, q => q.Audio);
            all.Add(Qemu("QemuPortForwards", "portForwards", q => string.Join(" ", q.PortForwards), null,
                v => new JArray(ProjectConfig.SplitPortForwards(v).Cast<object>().ToArray())));
            all.Add(Qemu("QemuDisks", "disks", q => FormatDisks(q.Disks), null, v => ProjectConfig.DisksToJson(ParseDisks(v))));
            all.Add(Qemu("QemuExtraArgs", "extraArgs", q => q.ExtraArgs));
            return all;
        }

        private static KernelConfigSetting Qemu(string name, string key, Func<QemuConfig, string> read,
            Func<string, IReadOnlyList<QemuChoice>> choices = null, Func<string, JToken> toJson = null) =>
            new KernelConfigSetting(name, key, true, null, choices, read, toJson ?? (v => v));

        private static void PerArch(List<KernelConfigSetting> all, string name, string key,
            Func<string, IReadOnlyList<QemuChoice>> choices, Func<QemuConfig, string> read)
        {
            foreach (QemuChoice arch in Architectures)
            {
                all.Add(new KernelConfigSetting(PerArchName(name, arch.Value), key, true, arch.Value, choices, read, v => v));
            }
        }

        /// <summary>The property name of an architecture's variant of a setting.</summary>
        public static string PerArchName(string name, string arch) => name + (arch == "arm64" ? "Arm64" : "X64");

        public static KernelConfigSetting Find(string name) =>
            All.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>The setting's value as the launcher would use it, or null for an unknown name.</summary>
        public static string Get(string projectDir, string name)
        {
            KernelConfigSetting setting = Find(name);
            if (setting == null)
            {
                return null;
            }
            if (!setting.IsQemu)
            {
                return CosmosProject.ReadTargetArch(projectDir);
            }
            string arch = setting.Arch ?? CosmosProject.ReadTargetArch(projectDir);
            return setting.Read(ProjectConfig.LoadQemuConfig(projectDir, arch)) ?? "";
        }

        /// <summary>True when config.json holds the setting, false when it falls back to the default.</summary>
        public static bool IsSet(string projectDir, string name)
        {
            KernelConfigSetting setting = Find(name);
            return setting != null && Section(CosmosProject.ReadConfig(projectDir), setting)?[setting.Key] != null;
        }

        private static JObject Section(JObject config, KernelConfigSetting setting) =>
            setting.IsQemu ? config?["qemu"] as JObject : config;

        /// <summary>Writes the setting, keeping every other key of config.json.</summary>
        public static void Set(string projectDir, string name, string value)
        {
            KernelConfigSetting setting = Find(name) ?? throw new ArgumentException("Unknown Cosmos setting: " + name, nameof(name));
            lock (WriteLock)
            {
                CosmosProject.UpdateConfig(projectDir, config =>
                {
                    if (setting.IsQemu && !(config["qemu"] is JObject))
                    {
                        config["qemu"] = new JObject();
                    }
                    Section(config, setting)[setting.Key] = setting.ToJson(value ?? "");
                });
            }
        }

        /// <summary>Removes the setting from config.json, so it falls back to the default.</summary>
        public static void Reset(string projectDir, string name)
        {
            KernelConfigSetting setting = Find(name);
            lock (WriteLock)
            {
                if (IsSet(projectDir, name))
                {
                    CosmosProject.UpdateConfig(projectDir, config => Section(config, setting).Remove(setting.Key));
                }
            }
        }

        /// <summary>
        /// One disk per line: the image path, then the controller and the size
        /// when they differ from AHCI and 256M (<c>data.img nvme 1G</c>).
        /// </summary>
        public static string FormatDisks(IEnumerable<DiskConfig> disks) =>
            string.Join(Environment.NewLine, disks.Select(d =>
                d.Path + (d.Type == "ahci" ? "" : " " + d.Type) + (d.Size == "256M" ? "" : " " + d.Size)));

        /// <summary>
        /// Parses <see cref="FormatDisks"/>. The size and controller are read
        /// off the end of the line, so a path may hold spaces; blank lines are
        /// skipped.
        /// </summary>
        public static List<DiskConfig> ParseDisks(string text)
        {
            var disks = new List<DiskConfig>();
            foreach (string line in Regex.Split(text ?? "", "\r?\n"))
            {
                List<string> words = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                if (words.Count == 0)
                {
                    continue;
                }
                var disk = new DiskConfig();
                if (words.Count > 1 && Regex.IsMatch(words[words.Count - 1], "^\\d+[KkMmGg]?$"))
                {
                    disk.Size = words[words.Count - 1];
                    words.RemoveAt(words.Count - 1);
                }
                QemuChoice type = words.Count > 1
                    ? QemuCatalog.DiskTypes.FirstOrDefault(t => t.Value.Equals(words[words.Count - 1], StringComparison.OrdinalIgnoreCase))
                    : null;
                if (type != null)
                {
                    disk.Type = type.Value;
                    words.RemoveAt(words.Count - 1);
                }
                disk.Path = string.Join(" ", words);
                disks.Add(disk);
            }
            return disks;
        }
    }
}
