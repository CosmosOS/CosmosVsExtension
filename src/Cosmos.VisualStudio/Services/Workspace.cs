using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cosmos.VisualStudio.Core;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SolutionEvents = Microsoft.VisualStudio.Shell.Events.SolutionEvents;

namespace Cosmos.VisualStudio.Services
{
    /// <summary>
    /// What is open in Visual Studio — a solution or a folder — and which Cosmos
    /// kernel project the commands act on.
    /// </summary>
    internal sealed class Workspace
    {
        private readonly ConcurrentDictionary<string, (DateTime Stamp, bool IsCosmos)> csprojCache =
            new ConcurrentDictionary<string, (DateTime, bool)>(StringComparer.OrdinalIgnoreCase);

        public Workspace()
        {
            // Solution events are raised on the UI thread.
#pragma warning disable VSTHRD010
            SolutionEvents.OnAfterOpenSolution += (s, e) => OnSolutionEvent();
            SolutionEvents.OnAfterCloseSolution += (s, e) => OnSolutionEvent();
            SolutionEvents.OnAfterOpenProject += (s, e) => OnSolutionEvent();
            // There is no after-close event; refresh once the close has finished.
            SolutionEvents.OnBeforeCloseProject += (s, e) => ThreadHelper.JoinableTaskFactory.StartOnIdle(Refresh).FileAndForget("Cosmos/Workspace");
            SolutionEvents.OnAfterLoadProject += (s, e) => OnSolutionEvent();
            SolutionEvents.OnAfterOpenFolder += (s, e) => OnSolutionEvent();
            SolutionEvents.OnAfterCloseFolder += (s, e) => OnSolutionEvent();
#pragma warning restore VSTHRD010
        }

        private void OnSolutionEvent()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Refresh();
        }

        /// <summary>Raised on the UI thread when the solution, folder or its projects change.</summary>
        public event EventHandler Changed;

        private ProjectInfo currentProject;
        private DateTime currentProjectAt = DateTime.MinValue;

        /// <summary>
        /// The kernel the Cosmos menu acts on. Command status asks for it on every
        /// idle tick, so it is re-resolved at most every few seconds; a change (a
        /// new startup project, a kernel created on disk, a switched architecture)
        /// raises <see cref="Changed"/>.
        /// </summary>
        public ProjectInfo CurrentProject
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (DateTime.UtcNow - currentProjectAt > TimeSpan.FromSeconds(3))
                {
                    ProjectInfo found = FindProject();
                    currentProjectAt = DateTime.UtcNow;
                    if (!SameProject(found, currentProject))
                    {
                        currentProject = found;
                        Publish();
                    }
                }
                return currentProject;
            }
        }

        private static bool SameProject(ProjectInfo a, ProjectInfo b) =>
            a == null ? b == null : b != null && string.Equals(a.Csproj, b.Csproj, StringComparison.OrdinalIgnoreCase) && a.Arch == b.Arch;

        /// <summary>Re-resolves the kernel project now and updates the Cosmos UI context.</summary>
        public void Refresh()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            currentProject = FindProject();
            currentProjectAt = DateTime.UtcNow;
            Publish();
        }

        private void Publish()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            UIContext.FromUIContextGuid(PackageGuids.ProjectContext).IsActive = currentProject != null;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        private static IVsSolution Solution
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return Package.GetGlobalService(typeof(SVsSolution)) as IVsSolution;
            }
        }

        /// <summary>The solution's directory, or the root of the open folder.</summary>
        public string SolutionDirectory
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                IVsSolution solution = Solution;
                if (solution == null)
                {
                    return null;
                }
                if (solution.GetProperty((int)__VSPROPID.VSPROPID_SolutionDirectory, out object dir) == VSConstants.S_OK &&
                    dir is string path && !string.IsNullOrEmpty(path))
                {
                    return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
                return null;
            }
        }

        public bool IsOpenFolder
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                IVsSolution solution = Solution;
                return solution != null &&
                       solution.GetProperty((int)__VSPROPID7.VSPROPID_IsInOpenFolderMode, out object value) == VSConstants.S_OK &&
                       value is bool open && open;
            }
        }

        /// <summary>Directories searched for kernel projects, test suites and build artifacts.</summary>
        public IReadOnlyList<string> Roots
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var roots = new List<string>();
                string dir = SolutionDirectory;
                if (dir != null)
                {
                    roots.Add(dir);
                }
                foreach (string project in LoadedProjectPaths())
                {
                    string projectDir = Path.GetDirectoryName(project);
                    if (!roots.Any(r => IsUnder(projectDir, r)))
                    {
                        roots.Add(projectDir);
                    }
                }
                return roots;
            }
        }

        private static bool IsUnder(string path, string root) =>
            path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        public IEnumerable<string> LoadedProjectPaths()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            IVsSolution solution = Solution;
            if (solution == null)
            {
                yield break;
            }
            Guid any = Guid.Empty;
            if (solution.GetProjectEnum((uint)__VSENUMPROJFLAGS.EPF_LOADEDINSOLUTION, ref any, out IEnumHierarchies hierarchies) != VSConstants.S_OK)
            {
                yield break;
            }
            var batch = new IVsHierarchy[1];
            while (hierarchies.Next(1, batch, out uint fetched) == VSConstants.S_OK && fetched == 1)
            {
                string path = ProjectPath(batch[0]);
                if (path != null)
                {
                    yield return path;
                }
            }
        }

        /// <summary>The project file of a hierarchy, or null for solution folders and the like.</summary>
        public static string ProjectPath(IVsHierarchy hierarchy)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (hierarchy is IVsProject project &&
                project.GetMkDocument((uint)VSConstants.VSITEMID.Root, out string path) == VSConstants.S_OK &&
                !string.IsNullOrEmpty(path) && path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            {
                return path;
            }
            return null;
        }

        public string StartupProjectPath
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (Package.GetGlobalService(typeof(SVsSolutionBuildManager)) is IVsSolutionBuildManager build &&
                    build.get_StartupProject(out IVsHierarchy startup) == VSConstants.S_OK && startup != null)
                {
                    return ProjectPath(startup);
                }
                return null;
            }
        }

        /// <summary>The project selected in Solution Explorer (or owning the selected item).</summary>
        public string SelectedProjectPath
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (!(Package.GetGlobalService(typeof(SVsShellMonitorSelection)) is IVsMonitorSelection selection))
                {
                    return null;
                }
                IntPtr hierarchyPtr = IntPtr.Zero;
                IntPtr containerPtr = IntPtr.Zero;
                try
                {
                    if (selection.GetCurrentSelection(out hierarchyPtr, out _, out _, out containerPtr) != VSConstants.S_OK ||
                        hierarchyPtr == IntPtr.Zero)
                    {
                        return null;
                    }
                    var hierarchy = System.Runtime.InteropServices.Marshal.GetObjectForIUnknown(hierarchyPtr) as IVsHierarchy;
                    return hierarchy == null ? null : ProjectPath(hierarchy);
                }
                finally
                {
                    if (hierarchyPtr != IntPtr.Zero) System.Runtime.InteropServices.Marshal.Release(hierarchyPtr);
                    if (containerPtr != IntPtr.Zero) System.Runtime.InteropServices.Marshal.Release(containerPtr);
                }
            }
        }

        /// <summary>True when the csproj is a Cosmos kernel; cached until the file changes.</summary>
        public bool IsCosmosProject(string csproj)
        {
            if (string.IsNullOrEmpty(csproj))
            {
                return false;
            }
            DateTime stamp;
            try
            {
                stamp = File.GetLastWriteTimeUtc(csproj);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException)
            {
                return false;
            }
            if (csprojCache.TryGetValue(csproj, out var cached) && cached.Stamp == stamp)
            {
                return cached.IsCosmos;
            }
            bool isCosmos = CosmosProject.IsCosmosCsproj(csproj);
            csprojCache[csproj] = (stamp, isCosmos);
            return isCosmos;
        }

        /// <summary>
        /// The kernel the Cosmos menu acts on: the startup project when it is a
        /// kernel, else the first loaded kernel project, else the first one found
        /// on disk under the solution (or folder) root.
        /// </summary>
        public ProjectInfo FindProject()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string startup = StartupProjectPath;
            if (IsCosmosProject(startup))
            {
                return CosmosProject.Load(startup);
            }
            foreach (string path in LoadedProjectPaths())
            {
                if (IsCosmosProject(path))
                {
                    return CosmosProject.Load(path);
                }
            }
            string dir = SolutionDirectory;
            return dir == null ? null : CosmosProject.Find(new[] { dir });
        }

        /// <summary>The selected Solution Explorer project, when it is a kernel.</summary>
        public ProjectInfo FindSelectedProject()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string selected = SelectedProjectPath;
            return IsCosmosProject(selected) ? CosmosProject.Load(selected) : null;
        }

        /// <summary>
        /// Whether F5 / Ctrl+F5 belong to the kernel: the startup project is one,
        /// or an open folder is itself a kernel project.
        /// </summary>
        public ProjectInfo FindLaunchProject()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            string startup = StartupProjectPath;
            if (startup != null)
            {
                return IsCosmosProject(startup) ? CosmosProject.Load(startup) : null;
            }
            if (IsOpenFolder && SolutionDirectory is string root)
            {
                string csproj = Directory.GetFiles(root, "*.csproj").FirstOrDefault(IsCosmosProject);
                return csproj == null ? null : CosmosProject.Load(csproj);
            }
            return null;
        }
    }
}
