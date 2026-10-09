using System;

namespace Cosmos.VisualStudio
{
    /// <summary>GUIDs shared with CosmosPackage.vsct and the package registration.</summary>
    internal static class PackageGuids
    {
        public const string PackageString = "77e2f7e6-2f56-45de-9e83-82ec60bed79b";
        public const string CommandSetString = "1e5172f1-8e46-4021-83c3-bd4256282e6a";

        // Active while the open solution or folder contains a Cosmos kernel project.
        public const string ProjectContextString = "6cf6fa39-75e6-4b73-ba19-5ff68e0921fd";
        // Active during a Cosmos kernel debug session; the kernel views show with it.
        public const string DebuggingContextString = "da74fdbf-3afe-4681-9bb7-8b36d135d315";

        public const string BuildPaneString = "2a8431fb-d529-45d9-92b0-63a43cb31d1c";
        public const string OutputPaneString = "f1eb59ff-8c7d-481a-ac68-cc2c0822bc1f";
        public const string TestsPaneString = "e0166489-c6de-4f43-a85d-f129586951cd";

        public const string ExplorerWindowString = "caa37f6e-a553-4a50-a915-4ffc2bd14811";
        public const string PropertiesWindowString = "e1fbf3c8-7611-44d8-8560-940816c9e52f";
        public const string ThreadsWindowString = "14759913-6544-4b88-9077-3a4cecda4c79";
        public const string GCWindowString = "ae289750-a554-4187-bfb6-35b424716947";
        public const string MemoryWindowString = "11398138-c1b6-4172-84ad-d9cbdef27cea";
        public const string MemoryMapWindowString = "d5d90cb0-8e39-44a1-ab4d-a79dc6ad223a";
        public const string TestsWindowString = "bf5956bd-6a7a-4672-9489-4ddbbff76cf2";

        public const string OptionsPageString = "7d7a5a5a-a875-450f-af5f-4ec35877c3b6";

        public static readonly Guid CommandSet = new Guid(CommandSetString);
        public static readonly Guid ProjectContext = new Guid(ProjectContextString);
        public static readonly Guid DebuggingContext = new Guid(DebuggingContextString);
        public static readonly Guid BuildPane = new Guid(BuildPaneString);
        public static readonly Guid OutputPane = new Guid(OutputPaneString);
        public static readonly Guid TestsPane = new Guid(TestsPaneString);

        // The MI debug engine (MIEngine) Visual Studio uses to drive gdb.
        public static readonly Guid MIEngine = new Guid("ea6637c6-17df-45b5-a183-0951c54243bc");
    }

    /// <summary>Command IDs, matching the IDSymbols of CosmosPackage.vsct.</summary>
    internal static class PackageIds
    {
        public const int NewProject = 0x0100;
        public const int Build = 0x0101;
        public const int Run = 0x0102;
        public const int Debug = 0x0103;
        public const int Stop = 0x0104;
        public const int Clean = 0x0105;
        public const int Properties = 0x0106;
        public const int CheckTools = 0x0107;
        public const int InstallTools = 0x0108;
        public const int ShowExplorer = 0x0109;
        public const int ShowTests = 0x010A;
        public const int ShowThreads = 0x010B;
        public const int ShowGC = 0x010C;
        public const int ShowMemory = 0x010D;
        public const int ShowMemoryMap = 0x010E;
        public const int ThreadsRefresh = 0x0110;
        public const int ThreadsCopy = 0x0111;
        public const int GCRefresh = 0x0112;
        public const int GCCopy = 0x0113;
        public const int MemoryRefresh = 0x0114;
        public const int MemoryCopy = 0x0115;

        // Solution Explorer project node variants: act on the selected project.
        public const int CtxBuild = 0x0120;
        public const int CtxRun = 0x0121;
        public const int CtxDebug = 0x0122;
        public const int CtxClean = 0x0123;
        public const int CtxProperties = 0x0124;

        public const int ThreadsToolbar = 0x1003;
        public const int GCToolbar = 0x1004;
        public const int MemoryToolbar = 0x1005;
    }
}
