using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Cosmos.VisualStudio.Core;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace Cosmos.VisualStudio.Services
{
    /// <summary>Builds and cleans kernels with <c>cosmos build</c>.</summary>
    internal sealed class KernelBuilder
    {
        private readonly CosmosPackage package;
        private readonly ErrorListProvider errorList;
        private StreamingProcess current;

        public KernelBuilder(CosmosPackage package)
        {
            this.package = package;
            errorList = new ErrorListProvider(package)
            {
                ProviderName = "Cosmos OS",
                ProviderGuid = PackageGuids.BuildPane
            };
        }

        public bool IsBuilding => current != null;

        /// <summary>Raised on the UI thread when a build starts or ends.</summary>
        public event EventHandler StateChanged;

        /// <summary>Debug or Release, following the solution configuration in the toolbar.</summary>
        public static string ActiveConfiguration()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (Package.GetGlobalService(typeof(SDTE)) is DTE dte)
                {
                    string name = dte.Solution?.SolutionBuild?.ActiveConfiguration?.Name;
                    if (name != null && name.IndexOf("Release", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return "Release";
                    }
                }
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // No solution configuration (e.g. Open Folder).
            }
            return "Debug";
        }

        /// <summary>Builds the kernel; true when <c>cosmos build</c> succeeded.</summary>
        public async Task<bool> BuildAsync(string csproj, string arch, string configuration)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            string name = Path.GetFileNameWithoutExtension(csproj);
            string projectDir = Path.GetDirectoryName(csproj);

            if (IsBuilding)
            {
                package.ShowMessage("A kernel build is already running.", OLEMSGICON.OLEMSGICON_INFO);
                return false;
            }
            string cosmos = CosmosTools.GetCosmosPath();
            if (cosmos == null)
            {
                package.ShowMessage("cosmos CLI not installed. Install Cosmos.Tools as a dotnet global tool (Cosmos > Install Tools).", OLEMSGICON.OLEMSGICON_CRITICAL);
                return false;
            }

            SaveAllDocuments();
            errorList.Tasks.Clear();

            OutputPane pane = Panes.Build;
            pane.Clear();
            pane.Show();
            pane.WriteLine($"Building {name} for {arch} ({configuration})...");
            pane.WriteLine();

            var args = new[] { "build", "-p", projectDir, "-a", arch, "-c", configuration, "-v" };
            pane.WriteLine("> cosmos " + CommandLine.Join(args));
            pane.WriteLine();

            ProcessStartInfo psi = CosmosTools.CreateStartInfo(cosmos, args, projectDir);
            // Wide virtual console so tools don't hard-wrap, and no interactive output.
            psi.Environment["COLUMNS"] = "1000";
            psi.Environment["CI"] = "true";

            var diagnostics = new List<BuildDiagnostic>();
            var seen = new HashSet<BuildDiagnostic>();
            var lines = new LineSplitter(line =>
            {
                BuildDiagnostic d = BuildDiagnostic.TryParse(line);
                if (d != null && seen.Add(d))
                {
                    lock (diagnostics)
                    {
                        diagnostics.Add(d);
                    }
                }
            });
            var log = new LogProcessor(text =>
            {
                pane.Write(text);
                lines.Append(text);
            }, join: true);

            StreamingProcess process;
            try
            {
                process = StreamingProcess.Start(psi, log.Append);
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception || e is CosmosException)
            {
                pane.WriteLine("Error: " + e.Message);
                package.ShowMessage("Build error: " + e.Message, OLEMSGICON.OLEMSGICON_CRITICAL);
                return false;
            }

            current = process;
            StateChanged?.Invoke(this, EventArgs.Empty);
            StatusBar.Animate(true);
            StatusBar.SetText($"Building {name} ({arch})...");

            int code = await process.Completion.ConfigureAwait(false);
            log.Flush();
            lines.Flush();

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            process.Dispose();
            current = null;
            StatusBar.Animate(false);
            StateChanged?.Invoke(this, EventArgs.Empty);

            int errors = ReportDiagnostics(diagnostics, projectDir);
            pane.WriteLine();
            if (code == 0)
            {
                pane.WriteLine("Build completed successfully.");
                StatusBar.SetText($"Build completed: {name} ({arch})");
                return true;
            }

            pane.WriteLine($"Build failed with exit code {code}");
            StatusBar.SetText($"Build failed with exit code {code}");
            if (errors > 0)
            {
                errorList.BringToFront();
            }
            return false;
        }

        public void Cancel()
        {
            current?.KillTree();
        }

        private int ReportDiagnostics(List<BuildDiagnostic> diagnostics, string projectDir)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            int errors = 0;
            errorList.SuspendRefresh();
            try
            {
                foreach (BuildDiagnostic d in diagnostics)
                {
                    string file = d.File;
                    if (!string.IsNullOrEmpty(file) && !Path.IsPathRooted(file))
                    {
                        file = Path.GetFullPath(Path.Combine(projectDir, file));
                    }
                    var task = new ErrorTask
                    {
                        Category = TaskCategory.BuildCompile,
                        ErrorCategory = d.IsError ? TaskErrorCategory.Error : TaskErrorCategory.Warning,
                        Text = string.IsNullOrEmpty(d.Code) ? d.Message : d.Code + ": " + d.Message,
                        Document = file,
                        Line = Math.Max(0, d.Line - 1),
                        Column = Math.Max(0, d.Column - 1)
                    };
                    if (!string.IsNullOrEmpty(file))
                    {
                        task.Navigate += (s, e) =>
                        {
                            ThreadHelper.ThrowIfNotOnUIThread();
                            errorList.Navigate(task, VSConstants.LOGVIEWID.Code_guid);
                        };
                    }
                    errorList.Tasks.Add(task);
                    if (d.IsError)
                    {
                        errors++;
                    }
                }
            }
            finally
            {
                errorList.ResumeRefresh();
            }
            return errors;
        }

        private static void SaveAllDocuments()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                (Package.GetGlobalService(typeof(SDTE)) as DTE)?.Documents?.SaveAll();
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Nothing to save, or a document refused; the build still runs.
            }
        }

        /// <summary>Deletes the kernel's build outputs after confirmation.</summary>
        public void Clean(ProjectInfo project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!package.Confirm("Delete all build outputs?"))
            {
                return;
            }

            int cleaned = 0;
            var failures = new List<string>();
            foreach (string dir in new[] { "output-x64", "output-arm64", "bin", "obj" })
            {
                string path = Path.Combine(project.ProjectDir, dir);
                if (!Directory.Exists(path))
                {
                    continue;
                }
                try
                {
                    Directory.Delete(path, recursive: true);
                    cleaned++;
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    failures.Add($"{dir}: {e.Message}");
                }
            }

            if (failures.Count > 0)
            {
                package.ShowMessage($"Cleaned {cleaned} directories. Could not delete:\n" + string.Join("\n", failures), OLEMSGICON.OLEMSGICON_WARNING);
            }
            else
            {
                StatusBar.SetText($"Cleaned {cleaned} directories");
            }
        }
    }
}
