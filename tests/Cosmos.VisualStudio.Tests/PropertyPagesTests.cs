using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Cosmos.VisualStudio.Core;
using Microsoft.Build.Framework.XamlTypes;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Cosmos.VisualStudio.Tests
{
    public class PropertyPagesTests
    {
        private static JObject Config(TempDir dir) => JObject.Parse(File.ReadAllText(CosmosProject.ConfigPath(dir.Path)));

        [Fact]
        public void SettingsReadDefaultsWithoutConfig()
        {
            using var dir = new TempDir();

            Assert.Equal("x64", KernelConfigSettings.Get(dir.Path, "TargetArch"));
            Assert.Equal("512M", KernelConfigSettings.Get(dir.Path, "QemuMemory"));
            Assert.Equal("q35", KernelConfigSettings.Get(dir.Path, "QemuMachineTypeX64"));
            Assert.Equal("virt", KernelConfigSettings.Get(dir.Path, "QemuMachineTypeArm64"));
            Assert.Equal("virtio-keyboard-device", KernelConfigSettings.Get(dir.Path, "QemuKeyboardArm64"));
            Assert.Equal("", KernelConfigSettings.Get(dir.Path, "QemuDisks"));
            Assert.False(KernelConfigSettings.IsSet(dir.Path, "QemuMemory"));
            Assert.Null(KernelConfigSettings.Get(dir.Path, "NoSuchSetting"));
            Assert.Throws<ArgumentException>(() => KernelConfigSettings.Set(dir.Path, "NoSuchSetting", "x"));
        }

        [Fact]
        public void SetWritesOneKeyAndKeepsTheRest()
        {
            using var dir = new TempDir();
            dir.Write(".cosmos/config.json", "{ \"targetArch\": \"x64\", \"custom\": 1, \"qemu\": { \"memory\": \"1G\", \"future\": true } }");

            KernelConfigSettings.Set(dir.Path, "QemuSerialMode", "none");
            KernelConfigSettings.Set(dir.Path, "TargetArch", "arm64");

            JObject config = Config(dir);
            Assert.Equal("arm64", (string)config["targetArch"]);
            Assert.Equal(1, (int)config["custom"]);
            Assert.Equal("none", (string)config["qemu"]["serialMode"]);
            Assert.Equal("1G", (string)config["qemu"]["memory"]);
            Assert.True((bool)config["qemu"]["future"]);
            Assert.Equal("arm64", CosmosProject.ReadTargetArch(dir.Path));
            Assert.True(KernelConfigSettings.IsSet(dir.Path, "QemuSerialMode"));

            KernelConfigSettings.Reset(dir.Path, "QemuSerialMode");
            Assert.Null(Config(dir)["qemu"]["serialMode"]);
            Assert.False(KernelConfigSettings.IsSet(dir.Path, "QemuSerialMode"));
            Assert.Equal("stdio", KernelConfigSettings.Get(dir.Path, "QemuSerialMode"));
        }

        [Fact]
        public void ArchitectureVariantsShareAKeyAndReadBackWhatTheLauncherUses()
        {
            using var dir = new TempDir();

            KernelConfigSettings.Set(dir.Path, "QemuNetworkCardX64", "e1000e");

            Assert.Equal("e1000e", (string)Config(dir)["qemu"]["networkCard"]);
            Assert.Equal("e1000e", KernelConfigSettings.Get(dir.Path, "QemuNetworkCardX64"));
            // arm64 has no E1000E driver: the launcher falls back to no card, and so does the page.
            Assert.Equal("none", KernelConfigSettings.Get(dir.Path, "QemuNetworkCardArm64"));
            Assert.Equal(ProjectConfig.LoadQemuConfig(dir.Path, "arm64").NetworkCard, KernelConfigSettings.Get(dir.Path, "QemuNetworkCardArm64"));
        }

        [Fact]
        public void PortForwardsAndDisksAreWrittenAsLists()
        {
            using var dir = new TempDir();

            KernelConfigSettings.Set(dir.Path, "QemuPortForwards", "tcp::2323-:23, udp::5000-:5000");
            KernelConfigSettings.Set(dir.Path, "QemuDisks", "boot.img\r\ndata disk.img nvme 1G\n\nlog.img 64M\n");

            JObject qemu = (JObject)Config(dir)["qemu"];
            Assert.Equal(new[] { "tcp::2323-:23", "udp::5000-:5000" }, qemu["portForwards"].Select(t => (string)t));
            Assert.Equal("tcp::2323-:23 udp::5000-:5000", KernelConfigSettings.Get(dir.Path, "QemuPortForwards"));

            List<DiskConfig> disks = ProjectConfig.LoadQemuConfig(dir.Path, "x64").Disks;
            Assert.Equal(new[] { "boot.img", "data disk.img", "log.img" }, disks.Select(d => d.Path));
            Assert.Equal(new[] { "ahci", "nvme", "ahci" }, disks.Select(d => d.Type));
            Assert.Equal(new[] { "256M", "1G", "64M" }, disks.Select(d => d.Size));
            Assert.Equal(string.Join(Environment.NewLine, "boot.img", "data disk.img nvme 1G", "log.img 64M"),
                KernelConfigSettings.Get(dir.Path, "QemuDisks"));
        }

        [Fact]
        public void DiskLinesKeepPathsThatLookLikeOptions()
        {
            List<DiskConfig> disks = KernelConfigSettings.ParseDisks("nvme\n1G\ndisk.img NVMe");

            Assert.Equal(new[] { "nvme", "1G", "disk.img" }, disks.Select(d => d.Path));
            Assert.Equal(new[] { "ahci", "ahci", "nvme" }, disks.Select(d => d.Type));
        }

        [Fact]
        public void PagesAreGenericRulesWithUniqueProperties()
        {
            IReadOnlyList<Rule> rules = KernelPropertyPages.CreateRules();

            Assert.Equal(new[] { KernelPropertyPages.KernelPage, KernelPropertyPages.QemuPage }, rules.Select(r => r.Name));
            foreach (Rule rule in rules)
            {
                Assert.Equal("generic", rule.PageTemplate);
                Assert.Equal(rule.Properties.Count, rule.Properties.Select(p => p.Name).Distinct().Count());
                Assert.All(rule.Properties, p => Assert.Contains(rule.Categories, c => c.Name == p.Category));
                Assert.All(rule.Properties, p => Assert.Same(rule, p.ContainingRule));
            }
        }

        [Fact]
        public void EveryConfigSettingIsOnAPageAndNothingElseUsesTheConfigSource()
        {
            List<BaseProperty> config = KernelPropertyPages.CreateRules()
                .SelectMany(r => r.Properties.Where(p => (p.DataSource ?? r.DataSource).Persistence == KernelPropertyPages.ConfigPersistence))
                .ToList();

            Assert.Equal(KernelConfigSettings.All.Select(s => s.Name).OrderBy(n => n), config.Select(p => p.Name).OrderBy(n => n));
        }

        [Fact]
        public void DropdownsListTheChoicesWithADriver()
        {
            Rule qemu = KernelPropertyPages.CreateRules().Single(r => r.Name == KernelPropertyPages.QemuPage);

            var arm64Cards = (EnumProperty)qemu.Properties.Single(p => p.Name == "QemuNetworkCardArm64");
            Assert.Equal(QemuCatalog.NetworkCards("arm64").Where(c => c.Enabled).Select(c => c.Value), arm64Cards.AdmissibleValues.Select(v => v.Name));
            Assert.DoesNotContain(arm64Cards.AdmissibleValues, v => v.Name == "e1000e");
            var x64Mice = (EnumProperty)qemu.Properties.Single(p => p.Name == "QemuMouseX64");
            Assert.Equal(QemuCatalog.Mice("x64").Where(c => c.Enabled).Select(c => c.Value), x64Mice.AdmissibleValues.Select(v => v.Name));
        }

        [Fact]
        public void ConditionsNameExistingPropertiesAndBalance()
        {
            IReadOnlyList<Rule> rules = KernelPropertyPages.CreateRules();
            var names = new HashSet<string>(rules.SelectMany(r => r.Properties.Select(p => r.Name + "::" + p.Name)));

            foreach (NameValuePair condition in rules.SelectMany(r => r.Properties).SelectMany(p => p.Metadata).Where(m => m.Name == "VisibilityCondition"))
            {
                Assert.Equal(condition.Value.Count(c => c == '('), condition.Value.Count(c => c == ')'));
                foreach (Match reference in Regex.Matches(condition.Value, "\\((?:has-evaluated-value|unevaluated) \"([^\"]+)\" \"([^\"]+)\""))
                {
                    Assert.Contains(reference.Groups[1].Value + "::" + reference.Groups[2].Value, names);
                }
            }
        }

        [Fact]
        public void FeatureSwitchesAreTheSdks()
        {
            Rule kernel = KernelPropertyPages.CreateRules().Single(r => r.Name == KernelPropertyPages.KernelPage);

            Assert.Equal(
                new[] { "Interrupts", "Timer", "Keyboard", "Mouse", "Network", "Scheduler", "PCI", "Storage", "Fat", "Usb", "Audio", "Graphics", "UART" }
                    .Select(f => "CosmosEnable" + f),
                kernel.Properties.OfType<BoolProperty>().Select(p => p.Name));
            Assert.Null(kernel.Properties.Single(p => p.Name == "CosmosKernelClass").DataSource);
            Assert.Equal("ProjectFile", kernel.DataSource.Persistence);
        }

        [Fact]
        public void DesignTimeTargetsGiveCosmosSdkProjectsTheCapability()
        {
            XDocument targets = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "BuildSystem", "Cosmos.DesignTime.targets"));

            XElement itemGroup = targets.Root.Elements("ItemGroup").Single();
            Assert.Equal("'$(UsingCosmosSdk)' == 'true'", (string)itemGroup.Attribute("Condition"));
            Assert.Equal(KernelPropertyPages.Capability, (string)itemGroup.Elements("ProjectCapability").Single().Attribute("Include"));
        }
    }
}
