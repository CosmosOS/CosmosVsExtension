using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Cosmos.VisualStudio.Core;
using Cosmos.VisualStudio.UI;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json.Linq;
using Task = System.Threading.Tasks.Task;

namespace Cosmos.VisualStudio.Services
{
    /// <summary>Creates a kernel project from the <c>cosmos-kernel</c> dotnet template.</summary>
    internal sealed class NewProjectService
    {
        private static readonly string[] Logo =
        {
            "  -hrr-                                       ___",
            "                                          ,o88888",
            "                                       ,o8888888'",
            "                 ,:o:o:oooo.        ,8O88Pd888\"",
            "             ,.::.::o:ooooOoOoO. ,oO8O8Pd888'",
            "           ,.:.::o:ooOoOoOO8O8OOo.8OOPd8O8O\"",
            "          , ..:.::o:ooOoOOOO8OOOOo.FdO8O8\"",
            "         , ..:.::o:ooOoOO8O888O8O,COCOO\"",
            "        , . ..:.::o:ooOoOOOO8OOOOCOCO\"",
            "        . ..:.::o:ooOoOoOO8O8OCCCC\"o",
            "           . ..:.::o:ooooOoCoCCC\"oo:o",
            "           . ..:.::o:o:,cooooCo\"oo:o:",
            "         `   . . ..:.:cocoooo\"'o:o:::'",
            "         .`   . ..::ccccoc\"'o:o:o:::'",
            "        :.:.    ,c:cccc\"':.:.:.:.:.'",
            "      ..:.:\"'`::::c:\"'..:.:.:.:.:.'",
            "    ...:.'.:.::::\"'    . . . . .'",
            "   .. . ....:.\"' `   .  . . ''",
            " . . . ....\"'",
            " .. . .\"'",
            "."
        };

        private readonly CosmosPackage package;

        public NewProjectService(CosmosPackage package)
        {
            this.package = package;
        }

        public async Task CreateAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!CosmosTools.IsCosmosInstalled)
            {
                if (package.Confirm("Cosmos Tools is required to create projects. Install now?"))
                {
                    package.Tools.OpenConsole("Cosmos Setup", "dotnet tool install -g Cosmos.Tools && cosmos install");
                    package.ShowMessage("Installing Cosmos Tools. Run New Kernel Project again after the installation completes.", OLEMSGICON.OLEMSGICON_INFO);
                }
                return;
            }

            OutputPane pane = Panes.Build;
            if (!await EnsureTemplatesAsync(pane))
            {
                return;
            }
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            string solutionDir = package.Workspace.SolutionDirectory;
            var dialog = new NewProjectDialog(
                solutionDir ?? DefaultProjectsLocation(),
                package.Options.DefaultArchitecture.ToString(),
                solutionOpen: solutionDir != null && !package.Workspace.IsOpenFolder);
            if (dialog.ShowModal() != true)
            {
                return;
            }

            string name = dialog.ProjectName;
            string arch = dialog.Architecture;
            string projectDir = dialog.ProjectDirectory;

            pane.Clear();
            pane.Show();
            pane.WriteLine("Creating Cosmos kernel project: " + name);
            pane.WriteLine("Architecture: " + arch);
            pane.WriteLine("Location: " + projectDir);
            pane.WriteLine();

            try
            {
                Directory.CreateDirectory(projectDir);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                package.ShowMessage("Failed to create project: " + e.Message, OLEMSGICON.OLEMSGICON_CRITICAL);
                return;
            }

            // -o keeps the files directly in projectDir (the template prefers a
            // name folder of its own otherwise).
            int code = await RunAsync(pane, projectDir, "new", "cosmos-kernel", "-n", name, "-o", projectDir, "--force");
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            string csproj = Path.Combine(projectDir, name + ".csproj");
            if (code != 0 || !File.Exists(csproj))
            {
                package.ShowMessage($"Failed to create project: dotnet new exited with code {code}. See the Cosmos OS - Build pane.", OLEMSGICON.OLEMSGICON_CRITICAL);
                return;
            }

            try
            {
                CosmosProject.UpdateConfig(projectDir, config =>
                {
                    config["targetArch"] = arch;
                    config["qemu"] = ProjectConfig.QemuToJson(ProjectConfig.DefaultQemuConfig(arch));
                });
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                pane.WriteLine("Warning: could not write .cosmos/config.json: " + e.Message);
            }

            pane.WriteLine();
            foreach (string line in Logo)
            {
                pane.WriteLine(line);
            }
            Match version = Regex.Match(File.ReadAllText(csproj), "<PackageReference\\s+Include=\"Cosmos.Kernel\"\\s+Version=\"([^\"]+)\"");
            pane.WriteLine("         Cosmos gen3 v" + (version.Success ? version.Groups[1].Value : ""));
            pane.WriteLine();
            pane.WriteLine($"Project created successfully! (Target: {arch})");

            if (dialog.AddToSolution)
            {
                AddToSolution(csproj);
            }
            else
            {
                await OpenInNewSolutionAsync(pane, projectDir, name, csproj);
            }
        }

        private async Task<bool> EnsureTemplatesAsync(OutputPane pane)
        {
            ProcessResult list = await Task.Run(() => CosmosTools.Run(CosmosTools.DotnetPath, new[] { "new", "list", "cosmos-kernel" }, timeoutMs: 60000));
            // "No templates found" also names the template, so the exit code decides.
            if (list.ExitCode == 0 && list.Output.Contains("cosmos-kernel"))
            {
                return true;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            pane.Clear();
            pane.Show();
            pane.WriteLine("Installing Cosmos templates...");
            int code = await RunAsync(pane, CosmosTools.HomeDirectory, "new", "install", "Cosmos.Build.Templates");
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (code != 0)
            {
                package.ShowMessage("Failed to install the Cosmos templates (dotnet new install Cosmos.Build.Templates). See the Cosmos OS - Build pane.", OLEMSGICON.OLEMSGICON_CRITICAL);
                return false;
            }
            pane.WriteLine();
            return true;
        }

        /// <summary>Runs <c>dotnet</c> with output streamed to the pane.</summary>
        private static async Task<int> RunAsync(OutputPane pane, string workingDirectory, params string[] args)
        {
            pane.WriteLine("> dotnet " + CommandLine.Join(args));
            var log = new LogProcessor(pane.Write);
            try
            {
                using (StreamingProcess process = StreamingProcess.Start(
                    CosmosTools.CreateStartInfo(CosmosTools.DotnetPath, args, workingDirectory), log.Append))
                {
#pragma warning disable VSTHRD003 // A TaskCompletionSource that never needs the UI thread.
                    int code = await process.Completion.ConfigureAwait(false);
#pragma warning restore VSTHRD003
                    log.Flush();
                    return code;
                }
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception || e is CosmosException)
            {
                pane.WriteLine("Error: " + e.Message);
                return -1;
            }
        }

        private void AddToSolution(string csproj)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                (Package.GetGlobalService(typeof(SDTE)) as DTE)?.Solution.AddFromFile(csproj);
                package.Workspace.Refresh();
                StatusBar.SetText($"Cosmos kernel \"{Path.GetFileNameWithoutExtension(csproj)}\" created successfully!");
            }
            catch (System.Runtime.InteropServices.COMException e)
            {
                package.ShowMessage("The project was created but could not be added to the solution: " + e.Message, OLEMSGICON.OLEMSGICON_WARNING);
            }
        }

        private async Task OpenInNewSolutionAsync(OutputPane pane, string projectDir, string name, string csproj)
        {
            string solution = FindSolution(projectDir, name);
            if (solution == null)
            {
                pane.WriteLine();
                await RunAsync(pane, projectDir, "new", "sln", "-n", name, "-o", projectDir);
                solution = FindSolution(projectDir, name);
                if (solution != null)
                {
                    await RunAsync(pane, projectDir, "sln", solution, "add", csproj);
                }
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (Package.GetGlobalService(typeof(SVsSolution)) is IVsSolution vsSolution)
            {
                // Without a solution file the project itself opens (VS wraps it in one).
                vsSolution.OpenSolutionFile(0, solution ?? csproj);
            }
        }

        private static string FindSolution(string dir, string name) =>
            new[] { name + ".slnx", name + ".sln" }
                .Select(f => Path.Combine(dir, f))
                .FirstOrDefault(File.Exists);

        private static string DefaultProjectsLocation()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (Package.GetGlobalService(typeof(SDTE)) is DTE dte &&
                    dte.Properties["Environment", "ProjectsAndSolution"].Item("ProjectsLocation").Value is string location &&
                    Directory.Exists(location))
                {
                    return location;
                }
            }
            catch (Exception e) when (e is System.Runtime.InteropServices.COMException || e is ArgumentException)
            {
            }
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }
    }
}
