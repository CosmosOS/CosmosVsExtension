using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace Cosmos.VisualStudio.Core
{
    /// <summary>The MIEngine launch options that attach gdb to QEMU's gdbstub.</summary>
    public static class DebugLaunchOptions
    {
        public const int GdbPort = 1234;

        private static readonly XNamespace Ns = "http://schemas.microsoft.com/vstudio/MDDDebuggerOptions/2014";

        /// <param name="printersScript">cosmos_prettyprint.py, sourced when gdb has Python; null to skip.</param>
        public static string Build(string elf, GdbChoice gdb, KernelTarget target, string printersScript)
        {
            // Run before target-select: without osabi none, gdb asks the stub for a
            // Windows TIB at connect time, which QEMU's gdbstub rejects.
            var commands = new List<(string Text, bool IgnoreFailures)> { ("set osabi none", false) };
            commands.Add(target.Arch == "arm64" ? ("set architecture aarch64", false) : ("set disassembly-flavor intel", true));
            if (gdb.HasPython && printersScript != null && File.Exists(printersScript))
            {
                // gdb's source takes the rest of the line as the file name, spaces included.
                commands.Add(("source " + printersScript.Replace('\\', '/'), true));
                commands.Add(("-enable-pretty-printing", true));
            }

            var options = new XElement(Ns + "LocalLaunchOptions",
                new XAttribute("MIDebuggerPath", gdb.Path),
                new XAttribute("MIDebuggerServerAddress", "localhost:" + GdbPort),
                new XAttribute("ExePath", elf),
                new XAttribute("WorkingDirectory", target.ProjectDir),
                new XAttribute("TargetArchitecture", target.Arch == "arm64" ? "arm64" : "x64"),
                new XAttribute("MIMode", "gdb"),
                new XAttribute("LaunchCompleteCommand", "exec-continue"),
                new XElement(Ns + "SetupCommands",
                    commands.Select(c => new XElement(Ns + "Command",
                        new XAttribute("IgnoreFailures", c.IgnoreFailures ? "true" : "false"),
                        c.Text))));
            return options.ToString(SaveOptions.DisableFormatting);
        }
    }
}
