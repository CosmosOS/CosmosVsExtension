using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Cosmos.VisualStudio.Core.Testing
{
    public sealed class TestRunOutcome
    {
        /// <summary>True when the engine returned 0.</summary>
        public bool Success { get; set; }
        /// <summary>Cases parsed from the engine's XML; empty on a hard failure.</summary>
        public List<JUnitCase> Cases { get; set; } = new List<JUnitCase>();
        /// <summary>Why no XML came back, or that the suite errored out.</summary>
        public string Error { get; set; }
    }

    /// <summary>Runs one kernel test suite through Cosmos.TestRunner.Engine.</summary>
    public static class TestRunner
    {
        /// <summary>
        /// The engine dll, building Cosmos.TestRunner.Engine once if it isn't
        /// built yet. Null when the project can't be found or doesn't build.
        /// </summary>
        public static async Task<string> EnsureEngineAsync(IReadOnlyList<string> roots, Action<string> output, CancellationToken token)
        {
            string existing = TestDiscovery.LocateTestRunnerDll(roots);
            if (existing != null)
            {
                return existing;
            }
            string csproj = TestDiscovery.LocateTestRunnerProject(roots);
            if (csproj == null)
            {
                output("error: could not find Cosmos.TestRunner.Engine.csproj\n");
                return null;
            }

            output("Building Cosmos.TestRunner.Engine (one-time)...\n");
            ProcessStartInfo psi = CosmosTools.CreateStartInfo(CosmosTools.DotnetPath, new[] { "build", csproj, "-c", "Debug" }, Path.GetDirectoryName(csproj));
            int code = await RunAsync(psi, output, token).ConfigureAwait(false);
            if (code != 0)
            {
                output($"dotnet build failed with exit code {code}\n");
                return null;
            }
            return TestDiscovery.LocateTestRunnerDll(roots);
        }

        /// <param name="mode">"ci" runs QEMU headless, "dev" shows its window.</param>
        public static async Task<TestRunOutcome> RunAsync(
            TestKernel kernel,
            string arch,
            string mode,
            IReadOnlyList<string> roots,
            Action<string> output,
            CancellationToken token,
            int? timeoutSeconds = null)
        {
            int timeout = timeoutSeconds ?? TestTimeouts.DefaultSeconds(kernel.SuiteName, arch);

            string engine = await EnsureEngineAsync(roots, output, token).ConfigureAwait(false);
            if (engine == null)
            {
                return new TestRunOutcome { Error = "Test runner engine not available" };
            }

            string xml = Path.Combine(Path.GetTempPath(),
                $"cosmos-test-{kernel.SuiteName}-{arch}-{Process.GetCurrentProcess().Id}-{DateTime.UtcNow.Ticks}.xml");
            var args = new[] { engine, kernel.ProjectDir, arch, timeout.ToString(CultureInfo.InvariantCulture), xml, mode };
            string cwd = roots.Count > 0 ? roots[0] : kernel.ProjectDir;

            output("> dotnet " + CommandLine.Join(args) + "\n");
            output($"(timeout {timeout}s, mode {mode})\n\n");

            int exitCode = await RunAsync(CosmosTools.CreateStartInfo(CosmosTools.DotnetPath, args, cwd), output, token).ConfigureAwait(false);

            try
            {
                if (token.IsCancellationRequested)
                {
                    output("\nrun cancelled\n");
                    return new TestRunOutcome { Error = "cancelled" };
                }

                JUnitSuite suite = JUnitParser.ParseFile(xml);
                if (suite == null)
                {
                    return new TestRunOutcome { Error = $"engine exited with code {exitCode} and produced no XML" };
                }
                if (suite.TimedOut)
                {
                    output("\nsuite timed out\n");
                }
                return new TestRunOutcome
                {
                    Success = exitCode == 0,
                    Cases = suite.Cases,
                    Error = suite.TimedOut ? "suite timed out" : null
                };
            }
            finally
            {
                try { File.Delete(xml); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private static async Task<int> RunAsync(ProcessStartInfo psi, Action<string> output, CancellationToken token)
        {
            var log = new LogProcessor(output);
            StreamingProcess process;
            try
            {
                process = StreamingProcess.Start(psi, log.Append);
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception || e is CosmosException)
            {
                output($"error: {e.Message}\n");
                return -1;
            }
            using (process)
            using (token.Register(process.KillTree))
            {
                int code = await process.Completion.ConfigureAwait(false);
                log.Flush();
                return code;
            }
        }
    }
}
