using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace Cosmos.VisualStudio.Core
{
    /// <summary>A Cosmos kernel project: the project file plus its configured architecture.</summary>
    public sealed class ProjectInfo
    {
        public ProjectInfo(string projectFile, string arch)
        {
            ProjectFile = projectFile;
            Arch = arch;
        }

        public string ProjectFile { get; }
        public string Arch { get; }
        public string Name => Path.GetFileNameWithoutExtension(ProjectFile);
        public string ProjectDir => Path.GetDirectoryName(ProjectFile);

        public static string ArchLabel(string arch) => arch == "arm64" ? "ARM64" : "x64";

        public static string ArchDescription(string arch) => arch == "arm64" ? "ARM 64-bit" : "Intel/AMD 64-bit";
    }

    /// <summary>Finds Cosmos kernel projects on disk.</summary>
    public static class CosmosProject
    {
        public const int MaxSearchDepth = 3;

        /// <summary>The project files a kernel can have, in the order <c>cosmos build</c> searches a directory: C#, then Visual Basic.</summary>
        public static readonly IReadOnlyList<string> ProjectExtensions = KernelLanguage.All.Select(language => language.ProjectExtension).ToArray();

        public static bool IsProjectFile(string path) =>
            ProjectExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

        /// <summary>The project files directly in <paramref name="dir"/>: the C# ones in name order, then the Visual Basic ones.</summary>
        public static IEnumerable<string> ProjectFilesIn(string dir) =>
            ProjectExtensions.SelectMany(extension => Directory.GetFiles(dir, "*" + extension).OrderBy(f => f, StringComparer.OrdinalIgnoreCase));

        public static List<string> FindProjectFiles(string dir, int depth = 0)
        {
            var results = new List<string>();
            if (depth > MaxSearchDepth || string.IsNullOrEmpty(dir))
            {
                return results;
            }
            try
            {
                results.AddRange(ProjectFilesIn(dir));
                foreach (string sub in Directory.GetDirectories(dir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
                {
                    string name = Path.GetFileName(sub);
                    if (name.StartsWith(".", StringComparison.Ordinal) || name == "node_modules" ||
                        name.Equals("bin", StringComparison.OrdinalIgnoreCase) || name.Equals("obj", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    results.AddRange(FindProjectFiles(sub, depth + 1));
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

        /// <summary>True when the project file references the Cosmos SDK or kernel packages.</summary>
        public static bool IsCosmosProject(string projectFile)
        {
            try
            {
                string content = File.ReadAllText(projectFile);
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
        /// Cosmos project, otherwise the first Cosmos project file under the roots.
        /// </summary>
        public static ProjectInfo Find(IEnumerable<string> roots, string preferred = null)
        {
            if (!string.IsNullOrEmpty(preferred) && File.Exists(preferred) && IsCosmosProject(preferred))
            {
                return Load(preferred);
            }
            foreach (string root in roots.Where(r => !string.IsNullOrEmpty(r)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (string projectFile in FindProjectFiles(root))
                {
                    if (IsCosmosProject(projectFile))
                    {
                        return Load(projectFile);
                    }
                }
            }
            return null;
        }

        public static ProjectInfo Load(string projectFile) => new ProjectInfo(projectFile, ReadTargetArch(Path.GetDirectoryName(projectFile)));

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
