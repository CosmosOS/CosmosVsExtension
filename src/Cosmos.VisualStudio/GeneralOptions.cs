using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace Cosmos.VisualStudio
{
    public enum KernelArchitecture
    {
        [Description("x64 (Intel/AMD 64-bit)")]
        x64,
        [Description("ARM64")]
        arm64
    }

    public enum TestMode
    {
        [Description("Headless QEMU (faster, no display)")]
        ci,
        [Description("Visual QEMU window (slower, useful for inspecting)")]
        dev
    }

    /// <summary>Tools &gt; Options &gt; Cosmos OS &gt; General.</summary>
    [Guid(PackageGuids.OptionsPageString)]
    public sealed class GeneralOptions : DialogPage
    {
        [Category("Projects")]
        [DisplayName("Default architecture")]
        [Description("Architecture preselected when creating a new kernel project.")]
        [DefaultValue(KernelArchitecture.x64)]
        public KernelArchitecture DefaultArchitecture { get; set; } = KernelArchitecture.x64;

        [Category("Run and Debug")]
        [DisplayName("Handle F5 and Ctrl+F5")]
        [Description("When the startup project is a Cosmos kernel, Start Debugging (F5) debugs it in QEMU and Start Without Debugging (Ctrl+F5) runs it.")]
        [DefaultValue(true)]
        public bool HandleStartCommands { get; set; } = true;

        [Category("Run and Debug")]
        [DisplayName("Build before running")]
        [Description("Build the kernel before Run or Debug when it was never built or its sources changed since the last build.")]
        [DefaultValue(true)]
        public bool BuildBeforeLaunch { get; set; } = true;

        [Category("Testing")]
        [DisplayName("Test mode")]
        [Description("How the Kernel Tests window runs QEMU: ci is headless, dev shows the QEMU window.")]
        [DefaultValue(TestMode.ci)]
        public TestMode TestMode { get; set; } = TestMode.ci;
    }
}
