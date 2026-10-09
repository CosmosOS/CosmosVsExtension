using System.IO;
using System.Linq;
using Cosmos.VisualStudio.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Cosmos.VisualStudio.Tests
{
    public class ProjectConfigTests
    {
        private const string Csproj = @"<Project Sdk=""Microsoft.NET.Sdk"">

  <Sdk Name=""Cosmos.Sdk"" Version=""3.0.54"" />

  <PropertyGroup>
    <CosmosKernelClass>MyKernel.Kernel</CosmosKernelClass>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <CosmosEnableAudio>false</CosmosEnableAudio>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include=""Cosmos.Kernel"" Version=""3.0.54"" />
    <PackageReference Include=""Cosmos.Kernel.Native.X64"" Version=""3.0.54"" />
    <PackageReference Include=""Cosmos.Build.Asm"" Version=""3.0.54"" />
    <PackageReference Include=""Cosmos.Kernel.System"" Version=""3.0.54"" />
  </ItemGroup>

</Project>
";

        [Fact]
        public void ParseReadsFeaturesPackagesAndDefaults()
        {
            using var dir = new TempDir();
            string csproj = dir.Write("MyKernel/MyKernel.csproj", Csproj);

            ProjectProperties props = ProjectConfig.Parse(csproj);

            Assert.Equal("MyKernel", props.Name);
            Assert.Equal("net10.0", props.TargetFramework);
            Assert.Equal("x64", props.TargetArch);
            Assert.Equal("MyKernel.Kernel", props.KernelClass);
            Assert.False(props.EnableAudio);
            Assert.True(props.EnableGraphics);
            Assert.Equal(new[] { "Cosmos.Kernel", "Cosmos.Kernel.System" }, props.Packages.Select(p => p.Name));
            Assert.Equal("q35", props.Qemu.MachineType);
            Assert.Equal("ps2", props.Qemu.Keyboard);
        }

        [Fact]
        public void SaveWritesOffSwitchesAndRemovesOnSwitches()
        {
            using var dir = new TempDir();
            string csproj = dir.Write("MyKernel/MyKernel.csproj", Csproj);
            ProjectProperties props = ProjectConfig.Parse(csproj);

            props.EnableAudio = true;
            props.EnableNetwork = false;
            props.GccFlags = "-O2 -DFOO=<1>";
            props.TargetArch = "arm64";
            ProjectConfig.Save(csproj, props);

            string content = File.ReadAllText(csproj);
            Assert.DoesNotContain("CosmosEnableAudio", content);
            Assert.Contains("<CosmosEnableNetwork>false</CosmosEnableNetwork>", content);
            Assert.Contains("<GCCCompilerFlags>-O2 -DFOO=&lt;1&gt;</GCCCompilerFlags>", content);
            // The removed property leaves no blank line behind.
            Assert.DoesNotContain("\n    \n", content);

            ProjectProperties reread = ProjectConfig.Parse(csproj);
            Assert.Equal("-O2 -DFOO=<1>", reread.GccFlags);
            Assert.Equal("arm64", reread.TargetArch);
            Assert.False(reread.EnableNetwork);
            Assert.True(reread.EnableAudio);

            props.GccFlags = "";
            ProjectConfig.Save(csproj, props);
            Assert.DoesNotContain("GCCCompilerFlags", File.ReadAllText(csproj));
        }

        [Fact]
        public void SetPropertyAddsToFirstPropertyGroupWithItsAttributes()
        {
            string content = "<Project>\n  <PropertyGroup Condition=\"x\">\n  </PropertyGroup>\n</Project>";
            string result = ProjectConfig.SetProperty(content, "CosmosEnableTimer", "false");
            Assert.Contains("<PropertyGroup Condition=\"x\">\n    <CosmosEnableTimer>false</CosmosEnableTimer>", result);
        }

        [Fact]
        public void LoadQemuConfigResetsDevicesTheArchitectureCannotUse()
        {
            using var dir = new TempDir();
            dir.Write(".cosmos/config.json", @"{
  ""targetArch"": ""arm64"",
  ""qemu"": {
    ""machineType"": ""q35"",
    ""cpuModel"": ""cortex-a53"",
    ""networkCard"": ""e1000e"",
    ""keyboard"": ""ps2"",
    ""audio"": ""intel-hda"",
    ""portForwards"": ""tcp::2323-:23, udp::5353-:53"",
    ""disks"": [ { ""path"": ""a.img"", ""type"": ""nvme"" }, { ""type"": ""ahci"" }, 42 ]
  }
}");
            QemuConfig q = ProjectConfig.LoadQemuConfig(dir.Path, "arm64");

            Assert.Equal("virt", q.MachineType);
            Assert.Equal("cortex-a53", q.CpuModel);
            Assert.Equal("none", q.NetworkCard);
            Assert.Equal("virtio-keyboard-device", q.Keyboard);
            Assert.Equal("none", q.Audio);
            Assert.Equal(new[] { "tcp::2323-:23", "udp::5353-:53" }, q.PortForwards);
            DiskConfig disk = Assert.Single(q.Disks);
            Assert.Equal("a.img", disk.Path);
            Assert.Equal("nvme", disk.Type);
            Assert.Equal("256M", disk.Size);
        }

        [Fact]
        public void SaveQemuConfigKeepsOtherKeys()
        {
            using var dir = new TempDir();
            dir.Write(".cosmos/config.json", "{ \"targetArch\": \"x64\", \"other\": 1, \"qemu\": { \"custom\": true } }");

            QemuConfig q = ProjectConfig.DefaultQemuConfig("x64");
            q.PortForwards.Add("tcp::8080-:80");
            ProjectConfig.SaveQemuConfig(dir.Path, q);

            JObject saved = JObject.Parse(File.ReadAllText(dir.Combine(".cosmos/config.json")));
            Assert.Equal(1, (int)saved["other"]!);
            Assert.True((bool)saved["qemu"]!["custom"]!);
            Assert.Equal("tcp::8080-:80", (string)saved["qemu"]!["portForwards"]![0]!);
            Assert.Equal("x64", CosmosProject.ReadTargetArch(dir.Path));
        }

        [Fact]
        public void FindPrefersTheRequestedProjectAndSkipsNonCosmosOnes()
        {
            using var dir = new TempDir();
            dir.Write("App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            string kernel = dir.Write("src/Kernel/Kernel.csproj", Csproj);
            dir.Write("src/Kernel/bin/Hidden/Hidden.csproj", Csproj);

            ProjectInfo found = CosmosProject.Find(new[] { dir.Path });
            Assert.NotNull(found);
            Assert.Equal(kernel, found!.Csproj);
            Assert.Equal("Kernel", found.Name);

            // A preferred project that isn't a kernel falls back to the search.
            Assert.Equal("Kernel", CosmosProject.Find(new[] { dir.Path }, dir.Combine("App/App.csproj"))!.Name);

            string second = dir.Write("Other/Other.csproj", Csproj);
            Assert.Equal(second, CosmosProject.Find(new[] { dir.Path }, second)!.Csproj);
        }
    }
}
