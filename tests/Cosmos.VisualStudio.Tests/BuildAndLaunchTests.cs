using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Cosmos.VisualStudio.Core;
using Xunit;

namespace Cosmos.VisualStudio.Tests
{
    public class BuildAndLaunchTests
    {
        [Fact]
        public void ParsesMsBuildDiagnosticWithLocation()
        {
            BuildDiagnostic d = BuildDiagnostic.TryParse(
                @"C:\src\MyKernel\Kernel.cs(12,9): error CS1002: ; expected [C:\src\MyKernel\MyKernel.csproj]");

            Assert.NotNull(d);
            Assert.True(d.IsError);
            Assert.Equal(@"C:\src\MyKernel\Kernel.cs", d.File);
            Assert.Equal(12, d.Line);
            Assert.Equal(9, d.Column);
            Assert.Equal("CS1002", d.Code);
            Assert.Equal("; expected", d.Message);
            Assert.Equal(@"C:\src\MyKernel\MyKernel.csproj", d.Project);
        }

        [Fact]
        public void ParsesMsBuildWarningWithRangeAndNoProject()
        {
            BuildDiagnostic d = BuildDiagnostic.TryParse("  Kernel.cs(3,1,3,20): warning CS0168: The variable 'x' is declared but never used");

            Assert.NotNull(d);
            Assert.False(d.IsError);
            Assert.Equal("Kernel.cs", d.File);
            Assert.Equal(3, d.Line);
            Assert.Equal(1, d.Column);
            Assert.Null(d.Project);
        }

        [Fact]
        public void ParsesGccDiagnostics()
        {
            BuildDiagnostic d = BuildDiagnostic.TryParse(@"C:\src\MyKernel\native\boot.c:42:7: fatal error: 'stdio.h' file not found");

            Assert.NotNull(d);
            Assert.True(d.IsError);
            Assert.Equal(@"C:\src\MyKernel\native\boot.c", d.File);
            Assert.Equal(42, d.Line);
            Assert.Equal(7, d.Column);
            Assert.Equal("'stdio.h' file not found", d.Message);

            BuildDiagnostic w = BuildDiagnostic.TryParse("native/io.c:5:12: warning: unused variable 'port'");
            Assert.False(w.IsError);
            Assert.Equal("native/io.c", w.File);
        }

        [Fact]
        public void ParsesMsBuildDiagnosticWithoutLocation()
        {
            BuildDiagnostic d = BuildDiagnostic.TryParse("CSC : error CS5001: Program does not contain a static 'Main' method [C:\\k\\K.csproj]");

            Assert.NotNull(d);
            Assert.Null(d.File);
            Assert.Equal("CS5001", d.Code);
            Assert.Equal(@"C:\k\K.csproj", d.Project);

            BuildDiagnostic bare = BuildDiagnostic.TryParse("error MSB1009: Project file does not exist.");
            Assert.NotNull(bare);
            Assert.Equal("MSB1009", bare.Code);
        }

        [Theory]
        [InlineData("")]
        [InlineData("Build succeeded.")]
        [InlineData("    0 Warning(s)")]
        [InlineData("  MyKernel -> C:\\out\\MyKernel.dll")]
        [InlineData("Compiling error handling module")]
        public void IgnoresOrdinaryLines(string line)
        {
            Assert.Null(BuildDiagnostic.TryParse(line));
        }

        [Fact]
        public void SummaryRepeatsCompareEqual()
        {
            const string line = @"C:\k\Kernel.cs(1,1): error CS0246: The type 'Foo' could not be found [C:\k\K.csproj]";
            var set = new HashSet<BuildDiagnostic> { BuildDiagnostic.TryParse(line), BuildDiagnostic.TryParse("  " + line) };

            Assert.Single(set);
        }

        [Fact]
        public void LineSplitterReassemblesChunks()
        {
            var lines = new List<string>();
            var splitter = new LineSplitter(lines.Add);

            splitter.Append("first li");
            splitter.Append("ne\r\nsecond\n\nthi");
            splitter.Append("rd");
            Assert.Equal(new[] { "first line", "second", "" }, lines);

            splitter.Flush();
            Assert.Equal("third", lines.Last());
            splitter.Flush();
            Assert.Equal(4, lines.Count);
        }

        [Fact]
        public void LaunchOptionsTargetX64Gdbstub()
        {
            using (var dir = new TempDir())
            {
                string script = dir.Write("Resources/gdb/cosmos_prettyprint.py", "# printers");
                var target = new KernelTarget(dir.Combine("MyKernel/MyKernel.csproj"), "x64", null);

                XElement options = XElement.Parse(DebugLaunchOptions.Build(
                    "/k/MyKernel.elf", new GdbChoice(@"C:\tools\gdb.exe", hasPython: true), target, script));

                XNamespace ns = "http://schemas.microsoft.com/vstudio/MDDDebuggerOptions/2014";
                Assert.Equal(ns + "LocalLaunchOptions", options.Name);
                Assert.Equal(@"C:\tools\gdb.exe", (string)options.Attribute("MIDebuggerPath"));
                Assert.Equal("localhost:1234", (string)options.Attribute("MIDebuggerServerAddress"));
                Assert.Equal("/k/MyKernel.elf", (string)options.Attribute("ExePath"));
                Assert.Equal(target.ProjectDir, (string)options.Attribute("WorkingDirectory"));
                Assert.Equal("x64", (string)options.Attribute("TargetArchitecture"));
                Assert.Equal("gdb", (string)options.Attribute("MIMode"));

                string[] commands = options.Element(ns + "SetupCommands").Elements(ns + "Command").Select(c => c.Value).ToArray();
                Assert.Equal("set osabi none", commands[0]);
                Assert.Equal("set disassembly-flavor intel", commands[1]);
                Assert.Equal("source " + script.Replace('\\', '/'), commands[2]);
                Assert.Equal("-enable-pretty-printing", commands[3]);
            }
        }

        [Fact]
        public void LaunchOptionsSkipPrintersWithoutPython()
        {
            var target = new KernelTarget("/k/Arm/Arm.csproj", "arm64", null);

            XElement options = XElement.Parse(DebugLaunchOptions.Build(
                "/k/Arm.elf", new GdbChoice("gdb-multiarch", hasPython: false), target, "/missing/printers.py"));

            XNamespace ns = "http://schemas.microsoft.com/vstudio/MDDDebuggerOptions/2014";
            Assert.Equal("arm64", (string)options.Attribute("TargetArchitecture"));
            string[] commands = options.Element(ns + "SetupCommands").Elements(ns + "Command").Select(c => c.Value).ToArray();
            Assert.Equal(new[] { "set osabi none", "set architecture aarch64" }, commands);
        }

        [Fact]
        public void RunCapturesOutputAndExitCode()
        {
            ProcessResult result = CosmosTools.Run(CosmosTools.DotnetPath, new[] { "--version" }, timeoutMs: 60000);

            Assert.False(result.TimedOut);
            Assert.Equal(0, result.ExitCode);
            Assert.Matches(@"^\d+\.\d+", result.Output.Trim());
        }

        [Fact]
        public void RunReportsMissingExecutable()
        {
            ProcessResult result = CosmosTools.Run("cosmos-no-such-tool-" + System.Guid.NewGuid().ToString("N"), new string[0]);

            Assert.Equal(-1, result.ExitCode);
            Assert.Equal("", result.Output);
        }
    }
}
