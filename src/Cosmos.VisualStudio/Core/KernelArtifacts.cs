using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Cosmos.VisualStudio.Core
{
    /// <summary>Finds the ISO and ELF a kernel build leaves behind.</summary>
    public static class KernelArtifacts
    {
        public static string OutputDirectory(string projectDir, string arch) => Path.Combine(projectDir, "output-" + arch);

        /// <summary>First ISO under <c>output-&lt;arch&gt;</c>, or null when that architecture isn't built.</summary>
        public static string FindIso(string projectDir, string arch)
        {
            string dir = OutputDirectory(projectDir, arch);
            if (!Directory.Exists(dir))
            {
                return null;
            }
            return Directory.GetFiles(dir, "*.iso").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        }

        /// <summary>
        /// Resolves the kernel ELF. Cosmos.Sdk routes outputs to a repo-level
        /// artifacts directory (<c>&lt;root&gt;/artifacts/bin/&lt;proj&gt;/debug_linux-&lt;arch&gt;</c>),
        /// where the root is wherever the repo's Directory.Build.props lives, so
        /// every folder from the project up to the workspace root is tried. Older
        /// layouts drop the binary under <c>bin/Debug/net10.0/linux-&lt;arch&gt;</c>.
        /// </summary>
        public static string ResolveKernelElf(IEnumerable<string> rootDirs, string projectDir, string projectName, string arch)
        {
            var candidates = new List<string>();
            foreach (string dir in SelfAndAncestors(projectDir))
            {
                candidates.Add(Path.Combine(dir, "artifacts", "bin", projectName, "debug_linux-" + arch));
            }
            foreach (string root in rootDirs ?? Enumerable.Empty<string>())
            {
                if (!string.IsNullOrEmpty(root))
                {
                    candidates.Add(Path.Combine(root, "artifacts", "bin", projectName, "debug_linux-" + arch));
                }
            }
            candidates.Add(Path.Combine(projectDir, "bin", "Debug", "net10.0", "linux-" + arch));

            foreach (string dir in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(dir))
                {
                    continue;
                }
                // Prefer "<projectName>.elf", otherwise the first ELF.
                string named = Path.Combine(dir, projectName + ".elf");
                if (File.Exists(named))
                {
                    return named;
                }
                string first = Directory.GetFiles(dir, "*.elf").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                if (first != null)
                {
                    return first;
                }
            }
            return null;
        }

        private static IEnumerable<string> SelfAndAncestors(string dir)
        {
            for (var info = new DirectoryInfo(dir); info != null; info = info.Parent)
            {
                yield return info.FullName;
            }
        }

        private static readonly string[] SourceExtensions = { ".cs", ".csproj", ".props", ".targets", ".c", ".h", ".asm", ".s", ".ld", ".conf" };

        private static readonly HashSet<string> SkippedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bin", "obj", "artifacts", "node_modules"
        };

        /// <summary>
        /// True when the ISO is missing or older than any source file of the
        /// project, i.e. launching it would boot a stale kernel.
        /// </summary>
        public static bool IsBuildOutOfDate(string projectDir, string arch)
        {
            string iso = FindIso(projectDir, arch);
            if (iso == null)
            {
                return true;
            }
            DateTime built = File.GetLastWriteTimeUtc(iso);
            return NewestSource(projectDir, 0) > built;
        }

        private static DateTime NewestSource(string dir, int depth)
        {
            DateTime newest = DateTime.MinValue;
            if (depth > 8)
            {
                return newest;
            }
            try
            {
                foreach (string file in Directory.GetFiles(dir))
                {
                    if (SourceExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    {
                        DateTime t = File.GetLastWriteTimeUtc(file);
                        if (t > newest)
                        {
                            newest = t;
                        }
                    }
                }
                foreach (string sub in Directory.GetDirectories(dir))
                {
                    string name = Path.GetFileName(sub);
                    if (name.StartsWith(".", StringComparison.Ordinal) || SkippedDirectories.Contains(name) ||
                        name.StartsWith("output-", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    DateTime t = NewestSource(sub, depth + 1);
                    if (t > newest)
                    {
                        newest = t;
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (IOException)
            {
            }
            return newest;
        }
    }
}
