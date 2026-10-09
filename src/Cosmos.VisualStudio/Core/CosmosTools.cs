using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Cosmos.VisualStudio.Core
{
    /// <summary>One entry of <c>cosmos check --json</c>.</summary>
    public sealed class ToolStatus
    {
        public string Name { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public bool Found { get; set; }
        public bool Required { get; set; }
        public string Version { get; set; }
        public string Path { get; set; }
    }

    public sealed class ProcessResult
    {
        public int ExitCode { get; set; }
        public string Output { get; set; } = "";
        public bool TimedOut { get; set; }
    }

    /// <summary>
    /// Locates the Cosmos CLI and the toolchain it installs (QEMU, GDB, LLVM…),
    /// and builds the environment child processes run with.
    /// </summary>
    public static class CosmosTools
    {
        public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        public static string HomeDirectory => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        public static string DotnetToolsDirectory => System.IO.Path.Combine(HomeDirectory, ".dotnet", "tools");

        // Where the Cosmos installer drops QEMU, GDB, LLVM and friends.
        public static string InstallerToolsDirectory
        {
            get
            {
                if (IsWindows)
                {
                    string localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
                    if (string.IsNullOrEmpty(localAppData))
                    {
                        localAppData = System.IO.Path.Combine(HomeDirectory, "AppData", "Local");
                    }
                    return System.IO.Path.Combine(localAppData, "Cosmos", "Tools");
                }
                return System.IO.Path.Combine(HomeDirectory, ".cosmos", "tools");
            }
        }

        /// <summary>The cosmos global tool, or null when Cosmos.Tools isn't installed.</summary>
        public static string GetCosmosPath()
        {
            string[] names = IsWindows ? new[] { "cosmos.exe", "cosmos" } : new[] { "cosmos" };
            foreach (string name in names)
            {
                string candidate = System.IO.Path.Combine(DotnetToolsDirectory, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            return null;
        }

        public static bool IsCosmosInstalled => GetCosmosPath() != null;

        /// <summary>
        /// PATH with the dotnet global tools and every Cosmos installer tool
        /// directory in front, so tools are found even when Visual Studio was
        /// started before the install updated the user's PATH.
        /// </summary>
        public static string GetPath()
        {
            string tools = InstallerToolsDirectory;
            var extra = new[]
            {
                DotnetToolsDirectory,
                System.IO.Path.Combine(tools, "bin"),
                System.IO.Path.Combine(tools, "llvm-tools", "bin"),
                System.IO.Path.Combine(tools, "yasm"),
                System.IO.Path.Combine(tools, "xorriso"),
                System.IO.Path.Combine(tools, "lld"),
                System.IO.Path.Combine(tools, "x86_64-elf-tools", "bin"),
                System.IO.Path.Combine(tools, "aarch64-elf-tools", "bin"),
                // The QEMU bundle keeps its executables in bin/ so QEMU's
                // <exec>/../share/qemu BIOS lookup resolves; the bare qemu entry
                // covers installs that predate that layout.
                System.IO.Path.Combine(tools, "qemu", "bin"),
                System.IO.Path.Combine(tools, "qemu"),
                // The gdb-multiarch zip extracts to gdb\bin, DLLs included.
                System.IO.Path.Combine(tools, "gdb", "bin")
            };
            string current = Environment.GetEnvironmentVariable("PATH") ?? "";
            return string.Join(System.IO.Path.PathSeparator.ToString(), extra) + System.IO.Path.PathSeparator + current;
        }

        /// <summary>Gives a process the PATH from <see cref="GetPath"/>.</summary>
        public static void ApplyEnvironment(ProcessStartInfo psi)
        {
            // The environment dictionary is case-insensitive on Windows; remove any
            // differently cased PATH key first so only ours survives elsewhere.
            foreach (string key in psi.Environment.Keys.Where(k => string.Equals(k, "PATH", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                psi.Environment.Remove(key);
            }
            psi.Environment["PATH"] = GetPath();
        }

        /// <summary>Finds an executable on the extended PATH.</summary>
        public static string FindCommand(string name)
        {
            string[] names = IsWindows
                ? new[] { name + ".exe", name + ".cmd", name + ".bat", name }
                : new[] { name };
            foreach (string dir in GetPath().Split(System.IO.Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir))
                {
                    continue;
                }
                foreach (string candidate in names)
                {
                    try
                    {
                        string full = System.IO.Path.Combine(dir.Trim(), candidate);
                        if (File.Exists(full))
                        {
                            return full;
                        }
                    }
                    catch (ArgumentException)
                    {
                        // Malformed PATH entry — skip it.
                    }
                }
            }
            return null;
        }

        public static string DotnetPath => FindCommand("dotnet") ?? "dotnet";

        public static ProcessStartInfo CreateStartInfo(string fileName, IEnumerable<string> args, string workingDirectory = null)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = CommandLine.Join(args),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            if (!string.IsNullOrEmpty(workingDirectory))
            {
                psi.WorkingDirectory = workingDirectory;
            }
            ApplyEnvironment(psi);
            return psi;
        }

        /// <summary>Runs a short-lived command and captures its combined output.</summary>
        public static ProcessResult Run(string fileName, IEnumerable<string> args, string workingDirectory = null, int timeoutMs = 10000)
        {
            var result = new ProcessResult { ExitCode = -1 };
            try
            {
                using (var process = new Process { StartInfo = CreateStartInfo(fileName, args, workingDirectory) })
                {
                    // Drain both pipes as data arrives so a chatty command can't fill
                    // one and stall past the timeout.
                    var output = new StringBuilder();
                    DataReceivedEventHandler collect = (s, e) =>
                    {
                        if (e.Data != null)
                        {
                            lock (output)
                            {
                                output.Append(e.Data).Append('\n');
                            }
                        }
                    };
                    process.OutputDataReceived += collect;
                    process.ErrorDataReceived += collect;
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    if (!process.WaitForExit(timeoutMs))
                    {
                        try { process.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
                        result.TimedOut = true;
                        return result;
                    }
                    // The parameterless wait also flushes the remaining output events.
                    process.WaitForExit();
                    result.ExitCode = process.ExitCode;
                    lock (output)
                    {
                        result.Output = output.ToString();
                    }
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Executable not found.
            }
            catch (InvalidOperationException)
            {
            }
            return result;
        }

        // Cached `cosmos check --json`. Filled lazily and by the Tools list's
        // refresh; the debugger reads the gdb-multiarch path out of it.
        private static readonly object CheckLock = new object();
        private static List<ToolStatus> cachedCheck;

        public static List<ToolStatus> GetToolsCheck()
        {
            lock (CheckLock)
            {
                if (cachedCheck != null)
                {
                    return cachedCheck;
                }
            }
            return RefreshToolsCheck();
        }

        /// <summary>Re-runs <c>cosmos check --json</c>; null when cosmos is missing or the check failed.</summary>
        public static List<ToolStatus> RefreshToolsCheck()
        {
            lock (CheckLock)
            {
                cachedCheck = null;
            }
            string cosmos = GetCosmosPath();
            if (cosmos == null)
            {
                return null;
            }
            // cosmos check exits non-zero when a required tool is missing, but the
            // JSON is still complete, so parse regardless of the exit code.
            ProcessResult run = Run(cosmos, new[] { "check", "--json" }, timeoutMs: 15000);
            List<ToolStatus> parsed = ParseToolsCheck(run.Output);
            lock (CheckLock)
            {
                cachedCheck = parsed;
            }
            return parsed;
        }

        public static List<ToolStatus> ParseToolsCheck(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }
            int start = json.IndexOf('{');
            int end = json.LastIndexOf('}');
            if (start < 0 || end <= start)
            {
                return null;
            }
            try
            {
                var root = JObject.Parse(json.Substring(start, end - start + 1));
                var tools = new List<ToolStatus>();
                if (root["tools"] is JArray array)
                {
                    foreach (JToken t in array)
                    {
                        tools.Add(new ToolStatus
                        {
                            Name = (string)t["name"] ?? "",
                            DisplayName = (string)t["displayName"] ?? (string)t["name"] ?? "",
                            Found = (bool?)t["found"] ?? false,
                            Required = (bool?)t["required"] ?? false,
                            Version = t["version"]?.Type == JTokenType.String ? (string)t["version"] : null,
                            Path = t["path"]?.Type == JTokenType.String ? (string)t["path"] : null
                        });
                    }
                }
                return tools;
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return null;
            }
        }

        /// <summary>The gdb-multiarch that <c>cosmos check</c> found, if any.</summary>
        public static string GetGdbPath()
        {
            ToolStatus gdb = GetToolsCheck()?.FirstOrDefault(t => t.Name == "gdb-multiarch");
            return gdb != null && gdb.Found && !string.IsNullOrEmpty(gdb.Path) ? gdb.Path : null;
        }

        /// <summary>Cosmos.Tools version from <c>dotnet tool list -g</c>.</summary>
        public static string GetCosmosToolsVersion()
        {
            ProcessResult run = Run(DotnetPath, new[] { "tool", "list", "-g" }, timeoutMs: 10000);
            return ParseToolVersion(run.Output, "cosmos.tools");
        }

        // Rows look like "cosmos.tools   3.0.37   cosmos".
        public static string ParseToolVersion(string toolList, string packageId)
        {
            foreach (string line in (toolList ?? "").Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith(packageId, StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    return parts.Length > 1 ? parts[1] : null;
                }
            }
            return null;
        }

        public static string PlatformName =>
            IsWindows ? "Windows" : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macOS" : "Linux";
    }
}
