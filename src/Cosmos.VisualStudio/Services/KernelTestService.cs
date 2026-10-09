using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cosmos.VisualStudio.Core;
using Cosmos.VisualStudio.Core.Testing;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace Cosmos.VisualStudio.Services
{
    internal sealed class TestCaseState
    {
        public string Name { get; set; } = "";
        public TestOutcome Outcome { get; set; }
        public string Message { get; set; }
        public double Seconds { get; set; }
    }

    internal sealed class TestSuiteState
    {
        public TestSuiteState(TestKernel kernel)
        {
            Kernel = kernel;
        }

        public TestKernel Kernel { get; set; }
        public string Name => Kernel.SuiteName;
        public TestOutcome Outcome { get; set; }
        public string Message { get; set; }
        public double? Seconds { get; set; }
        public List<TestCaseState> Cases { get; } = new List<TestCaseState>();
    }

    /// <summary>
    /// Discovers and runs the kernel test suites of a Cosmos source checkout
    /// (tests/Kernels/Cosmos.Kernel.Tests.*) through Cosmos.TestRunner.Engine.
    /// </summary>
    internal sealed class KernelTestService
    {
        private readonly CosmosPackage package;
        private CancellationTokenSource cts;

        public KernelTestService(CosmosPackage package)
        {
            this.package = package;
            package.Workspace.Changed += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                Refresh();
            };
        }

        public List<TestSuiteState> Suites { get; } = new List<TestSuiteState>();

        public bool IsRunning => cts != null;

        /// <summary>Raised on the UI thread when suites, outcomes or the running state change.</summary>
        public event EventHandler Changed;

        private void Raise() => Changed?.Invoke(this, EventArgs.Empty);

        /// <summary>Re-discovers suites, keeping the results of the ones still present.</summary>
        public void Refresh()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            List<TestKernel> found = TestDiscovery.FindTestKernels(package.Workspace.Roots);
            var byName = Suites.ToDictionary(s => s.Name);
            Suites.Clear();
            foreach (TestKernel kernel in found)
            {
                if (byName.TryGetValue(kernel.SuiteName, out TestSuiteState existing))
                {
                    existing.Kernel = kernel;
                    Suites.Add(existing);
                }
                else
                {
                    Suites.Add(new TestSuiteState(kernel));
                }
            }
            Raise();
        }

        public async Task RunAsync(IReadOnlyList<TestSuiteState> targets, string arch)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (IsRunning || targets.Count == 0)
            {
                return;
            }
            string mode = package.Options.TestMode.ToString();
            IReadOnlyList<string> roots = package.Workspace.Roots;
            cts = new CancellationTokenSource();
            CancellationToken token = cts.Token;

            foreach (TestSuiteState suite in targets)
            {
                suite.Outcome = TestOutcome.None;
                suite.Message = null;
            }
            OutputPane pane = Panes.Tests;
            pane.Clear();
            pane.Show();
            Raise();

            int passed = 0, failed = 0, skipped = 0;
            try
            {
                foreach (TestSuiteState suite in targets)
                {
                    if (token.IsCancellationRequested)
                    {
                        suite.Outcome = TestOutcome.Skipped;
                        suite.Message = "cancelled";
                        continue;
                    }
                    suite.Outcome = TestOutcome.Running;
                    Raise();
                    StatusBar.SetText($"Running kernel test suite {suite.Name} ({arch})...");
                    pane.WriteLine($"=== {suite.Name} ({arch}) ===");

                    var watch = Stopwatch.StartNew();
                    TestRunOutcome outcome = await TestRunner.RunAsync(suite.Kernel, arch, mode, roots, pane.Write, token);
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    Apply(suite, outcome, watch.Elapsed.TotalSeconds);
                    pane.WriteLine();
                    Raise();

                    passed += suite.Cases.Count(c => c.Outcome == TestOutcome.Passed);
                    failed += suite.Cases.Count(c => c.Outcome == TestOutcome.Failed) + (suite.Cases.Count == 0 ? 1 : 0);
                    skipped += suite.Cases.Count(c => c.Outcome == TestOutcome.Skipped);
                }
            }
            finally
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                cts.Dispose();
                cts = null;
                Raise();
            }
            StatusBar.SetText($"Kernel tests ({arch}): {passed} passed, {failed} failed, {skipped} skipped");
        }

        private static void Apply(TestSuiteState suite, TestRunOutcome outcome, double seconds)
        {
            suite.Seconds = seconds;
            if (outcome.Cases.Count == 0)
            {
                suite.Cases.Clear();
                suite.Outcome = outcome.Error == "cancelled" ? TestOutcome.Skipped : TestOutcome.Failed;
                suite.Message = outcome.Error ?? "Test run produced no results";
                return;
            }

            suite.Cases.Clear();
            suite.Cases.AddRange(outcome.Cases.Select(c => new TestCaseState
            {
                Name = c.Name,
                Outcome = c.Status,
                Message = c.Message,
                Seconds = c.TimeSeconds
            }));

            int failures = suite.Cases.Count(c => c.Outcome == TestOutcome.Failed);
            if (failures > 0 || !outcome.Success)
            {
                suite.Outcome = TestOutcome.Failed;
                suite.Message = outcome.Error ?? $"{failures} test(s) failed";
            }
            else
            {
                suite.Outcome = TestOutcome.Passed;
                suite.Message = null;
            }
        }

        /// <summary>Boots one suite's kernel under the debugger.</summary>
        public async Task DebugAsync(TestSuiteState suite, string arch)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var target = new KernelTarget(suite.Kernel.Csproj, arch, package.Workspace.Roots);
            await package.Launcher.DebugAsync(target);
        }

        public void Cancel()
        {
            cts?.Cancel();
        }
    }
}
