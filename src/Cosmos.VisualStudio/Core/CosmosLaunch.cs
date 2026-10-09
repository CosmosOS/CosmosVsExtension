using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Cosmos.VisualStudio.Core
{
    /// <summary>
    /// The kernel a launch boots: the open project's, or an explicit one (a test
    /// kernel picked in the Kernel Tests window).
    /// </summary>
    public sealed class KernelTarget
    {
        public KernelTarget(string csproj, string arch, IReadOnlyList<string> rootDirs)
        {
            Csproj = csproj;
            Arch = arch;
            RootDirs = rootDirs ?? Array.Empty<string>();
        }

        public string Csproj { get; }
        public string Arch { get; }
        // Workspace roots that may hold the artifacts/ directory Cosmos.Sdk writes to.
        public IReadOnlyList<string> RootDirs { get; }
        public string Name => Path.GetFileNameWithoutExtension(Csproj);
        public string ProjectDir => Path.GetDirectoryName(Csproj);
    }

    /// <summary>Everything needed to start <c>cosmos run</c> for one kernel.</summary>
    public sealed class LaunchSpec
    {
        public KernelTarget Target { get; set; }
        public string IsoPath { get; set; }
        public ProjectProperties Properties { get; set; }
        public string CosmosPath { get; set; }
        public List<string> Arguments { get; set; } = new List<string>();
        // Messages produced while preparing (created disk images, …).
        public List<string> Notes { get; set; } = new List<string>();

        public string DisplayCommand => CosmosPath + " " + CommandLine.Join(Arguments);
    }

    public static class CosmosLaunch
    {
        /// <summary>
        /// Builds the <c>cosmos run</c> command for <paramref name="target"/>. Run
        /// and Debug share it so the VM is the same either way; <paramref name="debug"/>
        /// adds <c>--debug</c> (QEMU waits for gdb on port 1234) and
        /// <paramref name="qemuPassthrough"/> puts raw QEMU flags (the QMP socket)
        /// in front of the project's own after <c>--</c>.
        /// </summary>
        public static LaunchSpec Prepare(KernelTarget target, bool debug, IEnumerable<string> qemuPassthrough = null)
        {
            if (!Directory.Exists(KernelArtifacts.OutputDirectory(target.ProjectDir, target.Arch)))
            {
                throw new CosmosException($"No build found for {target.Arch}. Build the kernel first.");
            }
            string iso = KernelArtifacts.FindIso(target.ProjectDir, target.Arch)
                ?? throw new CosmosException($"No ISO file found in output-{target.Arch}. Please build the project first.");
            string cosmos = CosmosTools.GetCosmosPath()
                ?? throw new CosmosException("cosmos CLI not installed. Install Cosmos.Tools as a dotnet global tool.");

            var spec = new LaunchSpec { Target = target, IsoPath = iso, CosmosPath = cosmos };
            List<string> args = spec.Arguments;
            args.AddRange(new[] { "run", "-a", target.Arch, "--iso", iso });
            if (debug)
            {
                args.Add("--debug");
            }

            try
            {
                spec.Properties = ProjectConfig.Parse(target.Csproj);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                spec.Notes.Add($"Could not read project properties ({e.Message}); using defaults.");
            }

            var extraArgs = new List<string>();
            ProjectProperties props = spec.Properties;
            if (props != null)
            {
                // Headless only when the project turned graphics off.
                if (!props.EnableGraphics)
                {
                    args.Add("--headless");
                }
                int? memoryMb = QemuOptions.ParseMemoryMb(props.Qemu.Memory);
                if (memoryMb != null)
                {
                    args.Add("-m");
                    args.Add(memoryMb.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                args.AddRange(QemuOptions.BuildCpuArgs(props.Qemu.CpuModel));
                args.AddRange(QemuOptions.BuildNicArgs(props.Qemu.NetworkCard));
                args.AddRange(QemuOptions.BuildHostForwardArgs(props.Qemu.PortForwards));
                args.AddRange(QemuOptions.BuildInputArgs(props.Qemu.Keyboard, props.Qemu.Mouse));
                args.AddRange(QemuOptions.BuildAudioArgs(props.Qemu.Audio));
                try
                {
                    args.AddRange(QemuOptions.PrepareDiskArgs(target.ProjectDir, props.Qemu.Disks, spec.Notes.Add));
                }
                catch (Exception e) when (e is CosmosException || e is IOException || e is UnauthorizedAccessException)
                {
                    throw new CosmosException("Failed to prepare disk image: " + e.Message);
                }
                extraArgs.AddRange(QemuOptions.SplitExtraArgs(props.Qemu.ExtraArgs));
            }

            // Raw QEMU flags go last, after `--`: cosmos run appends whatever
            // follows it to the QEMU command line as is.
            List<string> passthrough = (qemuPassthrough ?? Enumerable.Empty<string>()).Concat(extraArgs).ToList();
            if (passthrough.Count > 0)
            {
                args.Add("--");
                args.AddRange(passthrough);
            }
            return spec;
        }
    }
}
