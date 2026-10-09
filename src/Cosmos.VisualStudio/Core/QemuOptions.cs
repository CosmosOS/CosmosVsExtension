using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Cosmos.VisualStudio.Core
{
    /// <summary>Turns the project's QEMU configuration into <c>cosmos run</c> arguments.</summary>
    public static class QemuOptions
    {
        /// <summary>
        /// Memory strings like "512", "512M", "1G" in MB — the integer
        /// <c>cosmos run -m</c> expects.
        /// </summary>
        public static int? ParseMemoryMb(string s)
        {
            Match m = Regex.Match(s ?? "", "^(\\d+)\\s*([MmGg]?)");
            if (!m.Success || !int.TryParse(m.Groups[1].Value, out int n))
            {
                return null;
            }
            return m.Groups[2].Value.Equals("G", StringComparison.OrdinalIgnoreCase) ? n * 1024 : n;
        }

        /// <summary>
        /// Size strings like "256", "256M", "1G", "512K" in bytes. A bare number,
        /// or the M suffix, means mebibytes — how disk sizes are usually written.
        /// </summary>
        public static long? ParseSizeBytes(string s)
        {
            Match m = Regex.Match((s ?? "").Trim(), "^(\\d+)\\s*([KkMmGg]?)");
            if (!m.Success || !long.TryParse(m.Groups[1].Value, out long n))
            {
                return null;
            }
            switch (m.Groups[2].Value.ToUpperInvariant())
            {
                case "G": return n * 1024 * 1024 * 1024;
                case "K": return n * 1024;
                default: return n * 1024 * 1024;
            }
        }

        // An empty value yields no args, letting the launcher pick its default
        // (host under KVM, max under TCG on x64; cortex-a72 on arm64).
        public static IEnumerable<string> BuildCpuArgs(string cpuModel) =>
            string.IsNullOrWhiteSpace(cpuModel) ? Enumerable.Empty<string>() : new[] { "--cpu", cpuModel.Trim() };

        // "none" is passed through explicitly so QEMU's default NIC is disabled —
        // the whole point of the selector. Empty leaves QEMU's default in place.
        public static IEnumerable<string> BuildNicArgs(string networkCard) =>
            string.IsNullOrWhiteSpace(networkCard) ? Enumerable.Empty<string>() : new[] { "--nic", networkCard.Trim() };

        // One --hostfwd per rule (e.g. tcp::2323-:23). The launcher validates each
        // rule and puts it on the network card's user-mode backend.
        public static IEnumerable<string> BuildHostForwardArgs(IEnumerable<string> portForwards)
        {
            foreach (string rule in portForwards ?? Enumerable.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(rule))
                {
                    yield return "--hostfwd";
                    yield return rule.Trim();
                }
            }
        }

        // Values pass through as-is; the launcher treats ps2/none as "add
        // nothing" (x64 PS/2 is built into the chipset) and attaches only real
        // device models like virtio-keyboard-device.
        public static IEnumerable<string> BuildInputArgs(string keyboard, string mouse)
        {
            if (!string.IsNullOrWhiteSpace(keyboard))
            {
                yield return "--keyboard";
                yield return keyboard.Trim();
            }
            if (!string.IsNullOrWhiteSpace(mouse))
            {
                yield return "--mouse";
                yield return mouse.Trim();
            }
        }

        // "none" is the default and yields no args, so a project without audio
        // launches exactly the command it did before the selector existed.
        public static IEnumerable<string> BuildAudioArgs(string audio) =>
            string.IsNullOrWhiteSpace(audio) || audio.Trim() == "none"
                ? Enumerable.Empty<string>()
                : new[] { "--audio", audio.Trim() };

        /// <summary>
        /// Splits the free-form Extra Arguments field into the entries that follow
        /// <c>cosmos run --</c>: whitespace separates arguments, except inside
        /// double quotes. The quotes stay in the entry, since cosmos run joins the
        /// entries back into QEMU's command line where they still group the text.
        /// </summary>
        public static List<string> SplitExtraArgs(string extraArgs) =>
            Regex.Matches(extraArgs ?? "", "(?:[^\\s\"]+|\"[^\"]*\")+").Cast<Match>().Select(m => m.Value).ToList();

        /// <summary>
        /// Turns the configured disks into <c>--disk path,kind</c> arguments,
        /// creating any missing image as a sparse file of the requested size.
        /// Paths resolve against the project directory; existing images are never
        /// resized and rows without a path are skipped.
        /// </summary>
        public static List<string> PrepareDiskArgs(string projectDir, IEnumerable<DiskConfig> disks, Action<string> log = null)
        {
            var args = new List<string>();
            foreach (DiskConfig disk in disks ?? Enumerable.Empty<DiskConfig>())
            {
                if (string.IsNullOrWhiteSpace(disk.Path))
                {
                    continue;
                }
                string kind = disk.Type == "nvme" ? "nvme" : "ahci";
                string absPath = Path.IsPathRooted(disk.Path) ? disk.Path : Path.Combine(projectDir, disk.Path);

                if (!File.Exists(absPath))
                {
                    string sizeText = string.IsNullOrWhiteSpace(disk.Size) ? "256M" : disk.Size;
                    long? bytes = ParseSizeBytes(sizeText);
                    if (bytes == null || bytes <= 0)
                    {
                        throw new CosmosException($"Invalid disk size \"{sizeText}\" for {disk.Path}");
                    }
                    string dir = Path.GetDirectoryName(absPath);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }
                    using (var fs = new FileStream(absPath, FileMode.CreateNew, FileAccess.Write))
                    {
                        fs.SetLength(bytes.Value);
                    }
                    log?.Invoke($"Created disk image {absPath} ({sizeText})");
                }

                args.Add("--disk");
                args.Add(absPath + "," + kind);
            }
            return args;
        }
    }

    /// <summary>An error whose message is meant for the user as is.</summary>
    public sealed class CosmosException : Exception
    {
        public CosmosException(string message) : base(message)
        {
        }
    }
}
