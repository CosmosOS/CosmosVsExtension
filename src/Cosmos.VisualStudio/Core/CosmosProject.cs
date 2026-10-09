using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Cosmos.VisualStudio.Core
{
    /// <summary>A Cosmos kernel project: the csproj plus its configured architecture.</summary>
    public sealed class ProjectInfo
    {
        public ProjectInfo(string csproj, string arch)
        {
            Csproj = csproj;
            Arch = arch;
        }

        public string Csproj { get; }
        public string Arch { get; }
        public string Name => Path.GetFileNameWithoutExtension(Csproj);
        public string ProjectDir => Path.GetDirectoryName(Csproj);

        public static string ArchLabel(string arch) => arch == "arm64" ? "ARM64" : "x64";

        public static string ArchDescription(string arch) => arch == "arm64" ? "ARM 64-bit" : "Intel/AMD 64-bit";
    }

    /// <summary>Finds Cosmos kernel projects on disk.</summary>
    public static class CosmosProject
    {
        public const int MaxSearchDepth = 3;

        public static List<string> FindCsprojFiles(string dir, int depth = 0)
        {
            var results = new List<string>();
            if (depth > MaxSearchDepth || string.IsNullOrEmpty(dir))
            {
                return results;
            }
            try
            {
                foreach (string file in Directory.GetFiles(dir, "*.csproj").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    results.Add(file);
                }
                foreach (string sub in Directory.GetDirectories(dir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    string name = Path.GetFileName(sub);
                    if (name.StartsWith(".", StringComparison.Ordinal) || name == "node_modules" ||
                        name.Equals("bin", StringComparison.OrdinalIgnoreCase) || name.Equals("obj", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    results.AddRange(FindCsprojFiles(sub, depth + 1));
                }
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (IOException)
            {
            }
            return results;
        }

        /// <summary>True when the csproj references the Cosmos SDK or kernel packages.</summary>
        public static bool IsCosmosCsproj(string csproj)
        {
            try
            {
                string content = File.ReadAllText(csproj);
                return content.Contains("Cosmos.Sdk") || content.Contains("Cosmos.Kernel");
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// The kernel project to act on: <paramref name="preferred"/> when it is a
        /// Cosmos project, otherwise the first Cosmos csproj under the roots.
        /// </summary>
        public static ProjectInfo Find(IEnumerable<string> roots, string preferred = null)
        {
            if (!string.IsNullOrEmpty(preferred) && File.Exists(preferred) && IsCosmosCsproj(preferred))
            {
                return Load(preferred);
            }
            foreach (string root in roots.Where(r => !string.IsNullOrEmpty(r)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (string csproj in FindCsprojFiles(root))
                {
                    if (IsCosmosCsproj(csproj))
                    {
                        return Load(csproj);
                    }
                }
            }
            return null;
        }

        public static ProjectInfo Load(string csproj) => new ProjectInfo(csproj, ReadTargetArch(Path.GetDirectoryName(csproj)));

        public static string ConfigPath(string projectDir) => Path.Combine(projectDir, ".cosmos", "config.json");

        /// <summary>Architecture from <c>.cosmos/config.json</c>, x64 when unset.</summary>
        public static string ReadTargetArch(string projectDir)
        {
            JObject config = ReadConfig(projectDir);
            string arch = (string)config?["targetArch"];
            return string.IsNullOrEmpty(arch) ? "x64" : arch;
        }

        public static JObject ReadConfig(string projectDir)
        {
            string path = ConfigPath(projectDir);
            try
            {
                return File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : null;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is Newtonsoft.Json.JsonException)
            {
                return null;
            }
        }

        /// <summary>Reads the config (or starts an empty one), lets <paramref name="edit"/> change it, writes it back.</summary>
        public static void UpdateConfig(string projectDir, Action<JObject> edit)
        {
            string cosmosDir = Path.Combine(projectDir, ".cosmos");
            Directory.CreateDirectory(cosmosDir);
            JObject config = ReadConfig(projectDir) ?? new JObject();
            edit(config);
            File.WriteAllText(ConfigPath(projectDir), config.ToString(Newtonsoft.Json.Formatting.Indented));
        }
    }
}
