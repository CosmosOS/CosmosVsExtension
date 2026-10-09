using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Cosmos.VisualStudio.Core.Testing
{
    public sealed class TestKernel
    {
        public TestKernel(string suiteName, string projectDir, string csproj)
        {
            SuiteName = suiteName;
            ProjectDir = projectDir;
            Csproj = csproj;
        }

        public string SuiteName { get; }
        public string ProjectDir { get; }
        public string Csproj { get; }
    }

    /// <summary>
    /// Finds the kernel test suites of a Cosmos source checkout:
    /// <c>tests/Kernels/Cosmos.Kernel.Tests.&lt;Suite&gt;/Cosmos.Kernel.Tests.&lt;Suite&gt;.csproj</c>,
    /// run by <c>Cosmos.TestRunner.Engine</c>.
    /// </summary>
    public static class TestDiscovery
    {
        public const string KernelNamePrefix = "Cosmos.Kernel.Tests.";

        public static List<TestKernel> FindTestKernels(IEnumerable<string> roots)
        {
            var results = new List<TestKernel>();
            foreach (string root in Distinct(roots))
            {
                string kernelsRoot = Path.Combine(root, "tests", "Kernels");
                if (!Directory.Exists(kernelsRoot))
                {
                    continue;
                }
                string[] dirs;
                try
                {
                    dirs = Directory.GetDirectories(kernelsRoot);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    continue;
                }
                foreach (string dir in dirs)
                {
                    string name = Path.GetFileName(dir);
                    if (!name.StartsWith(KernelNamePrefix, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    string csproj = Path.Combine(dir, name + ".csproj");
                    if (File.Exists(csproj) && results.All(r => r.SuiteName != name.Substring(KernelNamePrefix.Length)))
                    {
                        results.Add(new TestKernel(name.Substring(KernelNamePrefix.Length), dir, csproj));
                    }
                }
            }
            return results.OrderBy(r => r.SuiteName, StringComparer.Ordinal).ToList();
        }

        public static string LocateTestRunnerDll(IEnumerable<string> roots) =>
            Distinct(roots)
                .Select(root => Path.Combine(root, "artifacts", "bin", "Cosmos.TestRunner.Engine", "debug", "Cosmos.TestRunner.Engine.dll"))
                .FirstOrDefault(File.Exists);

        public static string LocateTestRunnerProject(IEnumerable<string> roots) =>
            Distinct(roots)
                .Select(root => Path.Combine(root, "tests", "Cosmos.TestRunner.Engine", "Cosmos.TestRunner.Engine.csproj"))
                .FirstOrDefault(File.Exists);

        private static IEnumerable<string> Distinct(IEnumerable<string> roots) =>
            (roots ?? Enumerable.Empty<string>()).Where(r => !string.IsNullOrEmpty(r)).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Per-suite default timeouts in seconds. ARM64 boots and runs ~1.5x slower
    /// than x64 under TCG, so its timeouts are scaled accordingly.
    /// </summary>
    public static class TestTimeouts
    {
        private static readonly Dictionary<string, int> X64 = new Dictionary<string, int>
        {
            ["HelloWorld"] = 60,
            ["TypeCasting"] = 60,
            ["Memory"] = 60,
            ["Storage"] = 90,
            ["Timer"] = 120,
            ["Network"] = 120,
            ["Runtime"] = 120,
            ["Threading"] = 120,
            ["Graphic"] = 120,
            ["GarbageCollector"] = 120,
            ["Power"] = 180,
            ["Math"] = 60
        };

        private static readonly Dictionary<string, int> Arm64 = new Dictionary<string, int>
        {
            ["HelloWorld"] = 90,
            ["TypeCasting"] = 90,
            ["Memory"] = 90,
            ["Storage"] = 180,
            ["Timer"] = 180,
            ["Network"] = 180,
            ["Runtime"] = 180,
            ["Threading"] = 180,
            ["Graphic"] = 180,
            ["GarbageCollector"] = 180,
            ["Power"] = 270,
            ["Math"] = 90
        };

        public static int DefaultSeconds(string suiteName, string arch)
        {
            Dictionary<string, int> table = arch == "arm64" ? Arm64 : X64;
            return table.TryGetValue(suiteName, out int seconds) ? seconds : arch == "arm64" ? 180 : 120;
        }
    }
}
