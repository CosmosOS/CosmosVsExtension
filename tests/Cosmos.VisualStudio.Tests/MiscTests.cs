using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Cosmos.VisualStudio.Core;
using Cosmos.VisualStudio.Core.Testing;
using Xunit;

namespace Cosmos.VisualStudio.Tests
{
    public class MiscTests
    {
        [Fact]
        public void LogProcessorJoinsHardWrappedLines()
        {
            var sb = new StringBuilder();
            var log = new LogProcessor(s => sb.Append(s), join: true);

            log.Append("  Compiling C:\\very\\long\\pa");
            log.Append("th\\Kernel.cs\n");
            log.Append("th\\continued\r\n  next line\n\n");
            log.Append("\u001b[32m  green\u001b[0m");
            log.Flush();

            Assert.Equal("  Compiling C:\\very\\long\\path\\Kernel.csth\\continued\n  next line\n\n  green", sb.ToString());
        }

        [Fact]
        public void LogProcessorPassesLinesThroughWithoutJoin()
        {
            var parts = new List<string>();
            var log = new LogProcessor(parts.Add);
            log.Append("a\r\nb");
            log.Append("\nc");
            log.Flush();
            Assert.Equal(new[] { "a\n", "b\n", "c" }, parts);
        }

        [Fact]
        public void JUnitParserReadsCasesAndProperties()
        {
            string xml = @"<?xml version=""1.0""?>
<testsuites>
  <testsuite name=""HelloWorld"" tests=""3"" failures=""1"" skipped=""1"" time=""1.5"">
    <properties>
      <property name=""architecture"" value=""x64"" />
      <property name=""timedOut"" value=""true"" />
    </properties>
    <testcase name=""Boots"" classname=""Kernel"" time=""0.25"" />
    <testcase name=""Prints"" classname=""Kernel"" time=""0.5"">
      <failure message=""short""><![CDATA[expected <a> got <b>]]></failure>
    </testcase>
    <testcase name=""Later"" classname=""Kernel"" time=""0""><skipped message=""not yet"" /></testcase>
    <system-err><![CDATA[boom]]></system-err>
  </testsuite>
</testsuites>";

            JUnitSuite suite = JUnitParser.Parse(xml)!;

            Assert.Equal("HelloWorld", suite.Name);
            Assert.Equal("x64", suite.Architecture);
            Assert.True(suite.TimedOut);
            Assert.Equal(new[] { TestOutcome.Passed, TestOutcome.Failed, TestOutcome.Skipped }, suite.Cases.Select(c => c.Status));
            Assert.Equal("expected <a> got <b>", suite.Cases[1].Message);
            Assert.Equal("not yet", suite.Cases[2].Message);
            Assert.Equal(0.25, suite.Cases[0].TimeSeconds);
            Assert.Equal("boom", suite.SystemErr);
            Assert.Null(JUnitParser.Parse("<nope"));
        }

        [Fact]
        public void TestDiscoveryFindsSuitesAndEngine()
        {
            using var dir = new TempDir();
            dir.Write("tests/Kernels/Cosmos.Kernel.Tests.Timer/Cosmos.Kernel.Tests.Timer.csproj", "<Project />");
            dir.Write("tests/Kernels/Cosmos.Kernel.Tests.Memory/Cosmos.Kernel.Tests.Memory.csproj", "<Project />");
            dir.Write("tests/Kernels/Cosmos.Kernel.Tests.Empty/readme.md", "");
            dir.Write("tests/Kernels/Other/Other.csproj", "<Project />");
            dir.Write("tests/Cosmos.TestRunner.Engine/Cosmos.TestRunner.Engine.csproj", "<Project />");

            var kernels = TestDiscovery.FindTestKernels(new[] { dir.Path, dir.Path });

            Assert.Equal(new[] { "Memory", "Timer" }, kernels.Select(k => k.SuiteName));
            Assert.Null(TestDiscovery.LocateTestRunnerDll(new[] { dir.Path }));
            Assert.NotNull(TestDiscovery.LocateTestRunnerProject(new[] { dir.Path }));
            Assert.Equal(270, TestTimeouts.DefaultSeconds("Power", "arm64"));
            Assert.Equal(120, TestTimeouts.DefaultSeconds("Unknown", "x64"));
        }

        [Fact]
        public void KernelArtifactsFindsElfUnderAnyAncestorArtifacts()
        {
            using var dir = new TempDir();
            string projectDir = dir.Combine("examples/MyKernel");
            Directory.CreateDirectory(projectDir);
            dir.Write("artifacts/bin/MyKernel/debug_linux-x64/other.elf", "");
            string named = dir.Write("artifacts/bin/MyKernel/debug_linux-x64/MyKernel.elf", "");

            Assert.Equal(named, KernelArtifacts.ResolveKernelElf(new[] { dir.Path }, projectDir, "MyKernel", "x64"));
            Assert.Null(KernelArtifacts.ResolveKernelElf(new[] { dir.Path }, projectDir, "MyKernel", "arm64"));

            string legacy = dir.Write("examples/MyKernel/bin/Debug/net10.0/linux-arm64/kernel.elf", "");
            Assert.Equal(legacy, KernelArtifacts.ResolveKernelElf(Array.Empty<string>(), projectDir, "MyKernel", "arm64"));
        }

        [Fact]
        public void BuildIsOutOfDateWhenSourcesAreNewer()
        {
            using var dir = new TempDir();
            string source = dir.Write("Kernel.cs", "class K {}");
            Assert.True(KernelArtifacts.IsBuildOutOfDate(dir.Path, "x64"));

            string iso = dir.Write("output-x64/MyKernel.iso", "iso");
            File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(-10));
            File.SetLastWriteTimeUtc(iso, DateTime.UtcNow.AddMinutes(-5));
            Assert.False(KernelArtifacts.IsBuildOutOfDate(dir.Path, "x64"));

            File.SetLastWriteTimeUtc(source, DateTime.UtcNow);
            Assert.True(KernelArtifacts.IsBuildOutOfDate(dir.Path, "x64"));
        }

        [Fact]
        public void LaunchRequiresABuild()
        {
            using var dir = new TempDir();
            string csproj = dir.Write("MyKernel.csproj", "<Project><Sdk Name=\"Cosmos.Sdk\" /></Project>");
            var target = new KernelTarget(csproj, "x64", new[] { dir.Path });

            var e = Assert.Throws<CosmosException>(() => CosmosLaunch.Prepare(target, debug: false));
            Assert.Contains("No build found for x64", e.Message);

            Directory.CreateDirectory(dir.Combine("output-x64"));
            e = Assert.Throws<CosmosException>(() => CosmosLaunch.Prepare(target, debug: false));
            Assert.Contains("No ISO file found", e.Message);
        }
    }
}
