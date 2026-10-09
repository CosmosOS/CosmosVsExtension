using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text.RegularExpressions;

namespace Cosmos.VisualStudio.Core
{
    public sealed class GdbChoice
    {
        public GdbChoice(string path, bool hasPython)
        {
            Path = path;
            HasPython = hasPython;
        }

        public string Path { get; }
        public bool HasPython { get; }
    }

    public static class GdbLocator
    {
        private static readonly ConcurrentDictionary<string, bool> PythonCache = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, bool> ArchCache = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Picks the gdb to debug an <paramref name="arch"/> guest with.
        /// Architecture support gates the candidates first: a single-arch host gdb
        /// can't attach to an aarch64 guest even if it has Python. Among the rest,
        /// one with Python wins so the NativeAOT pretty-printers load (Cosmos's
        /// bundled gdb is built without it). Null when none qualifies.
        /// </summary>
        public static GdbChoice Locate(string arch)
        {
            var candidates = new[] { CosmosTools.GetGdbPath(), CosmosTools.FindCommand("gdb-multiarch"), CosmosTools.FindCommand("gdb") }
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(p => SupportsArch(p, arch))
                .ToList();
            if (candidates.Count == 0)
            {
                return null;
            }
            string withPython = candidates.FirstOrDefault(HasPython);
            return new GdbChoice(withPython ?? candidates[0], withPython != null);
        }

        public static string MissingGdbMessage(string arch) => arch == "arm64"
            ? "No gdb that can target aarch64 was found. Install a multiarch gdb (`cosmos install --auto --tools`)."
            : "gdb not found. Run `cosmos install --auto --tools`.";

        // True if gdb was built with Python AND its `gdb` module loads; Python
        // compiled in without the data directory can't run the printers either.
        private static bool HasPython(string gdbPath) => PythonCache.GetOrAdd(gdbPath, path =>
        {
            ProcessResult r = CosmosTools.Run(path, new[] { "-batch", "-ex", "python import gdb; print(\"ok\")" }, timeoutMs: 5000);
            return r.ExitCode == 0 &&
                   Regex.IsMatch(r.Output, "\\bok\\b") &&
                   r.Output.IndexOf("Python scripting is not supported", StringComparison.OrdinalIgnoreCase) < 0 &&
                   r.Output.IndexOf("No module named 'gdb'", StringComparison.OrdinalIgnoreCase) < 0;
        });

        // x64 guests attach from any host gdb; only arm64 needs a probe. A gdb
        // without the target prints 'Undefined item: "aarch64".'
        private static bool SupportsArch(string gdbPath, string arch)
        {
            if (arch != "arm64")
            {
                return true;
            }
            return ArchCache.GetOrAdd(arch + " " + gdbPath, _ =>
            {
                ProcessResult r = CosmosTools.Run(gdbPath, new[] { "-batch", "-ex", "set architecture aarch64" }, timeoutMs: 5000);
                return !r.TimedOut && r.ExitCode != -1 &&
                       !Regex.IsMatch(r.Output, "Undefined item|Undefined architecture|not a recognized", RegexOptions.IgnoreCase);
            });
        }
    }
}
