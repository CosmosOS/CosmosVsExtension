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
    /// <summary>Creates a kernel project, in C# or Visual Basic, from the Cosmos dotnet templates.</summary>
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
            ISet<string> templates = await EnsureTemplatesAsync(pane);
            if (templates == null)
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
            KernelLanguage language = dialog.KernelLanguage;
            string arch = dialog.Architecture;
            string projectDir = dialog.ProjectDirectory;

            string template = language.FindTemplate(templates);
            if (template == null)
            {
                // The templates from before the split by language have no Visual Basic one.
                if (package.Confirm($"The installed Cosmos templates have no {language.Name} template. Update Cosmos now?"))
                {
                    // The console opens in the home directory: --no-project keeps the update off the projects under it.
                    package.Tools.OpenConsole("Cosmos Update", "cosmos update --no-project");
                    package.ShowMessage("Updating Cosmos. Run New Kernel Project again after the update completes.", OLEMSGICON.OLEMSGICON_INFO);
                }
                return;
            }

            pane.Clear();
            pane.Show();
            pane.WriteLine("Creating Cosmos kernel project: " + name);
            pane.WriteLine("Language: " + language.Name);
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
            int code = await RunAsync(pane, projectDir, "new", template, "-n", name, "-o", projectDir, "--force");
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            string projectFile = Path.Combine(projectDir, name + language.ProjectExtension);
            if (code != 0 || !File.Exists(projectFile))
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
            Match version = Regex.Match(File.ReadAllText(projectFile), "<PackageReference\\s+Include=\"Cosmos.Kernel\"\\s+Version=\"([^\"]+)\"");
            pane.WriteLine("         Cosmos gen3 v" + (version.Success ? version.Groups[1].Value : ""));
            pane.WriteLine();
            pane.WriteLine($"Project created successfully! (Target: {arch})");

            if (dialog.AddToSolution)
            {
                AddToSolution(projectFile);
            }
            else
            {
                await OpenInNewSolutionAsync(pane, projectDir, name, projectFile);
            }
        }

        /// <summary>
        /// The short names of the installed kernel templates, installing the
        /// template package first when there are none. Null when that failed.
        /// </summary>
        private async Task<ISet<string>> EnsureTemplatesAsync(OutputPane pane)
        {
            ISet<string> templates = await ListTemplatesAsync();
            if (templates.Count > 0)
            {
                return templates;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            pane.Clear();
            pane.Show();
            pane.WriteLine("Installing Cosmos templates...");
            int code = await RunAsync(pane, CosmosTools.HomeDirectory, "new", "install", KernelTemplates.Package);
            if (code == 0)
            {
                templates = await ListTemplatesAsync();
            }
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (templates.Count == 0)
            {
                package.ShowMessage($"Failed to install the Cosmos templates (dotnet new install {KernelTemplates.Package}). See the Cosmos OS - Build pane.", OLEMSGICON.OLEMSGICON_CRITICAL);
                return null;
            }
            pane.WriteLine();
            return templates;
        }

        private static async Task<ISet<string>> ListTemplatesAsync()
        {
            ProcessResult list = await Task.Run(() => CosmosTools.Run(CosmosTools.DotnetPath, new[] { "new", "list", KernelTemplates.ListFilter }, timeoutMs: 60000));
            return KernelTemplates.Installed(list.Output);
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

        private void AddToSolution(string projectFile)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                (Package.GetGlobalService(typeof(SDTE)) as DTE)?.Solution.AddFromFile(projectFile);
                package.Workspace.Refresh();
                StatusBar.SetText($"Cosmos kernel \"{Path.GetFileNameWithoutExtension(projectFile)}\" created successfully!");
            }
            catch (System.Runtime.InteropServices.COMException e)
            {
                package.ShowMessage("The project was created but could not be added to the solution: " + e.Message, OLEMSGICON.OLEMSGICON_WARNING);
            }
        }

        private async Task OpenInNewSolutionAsync(OutputPane pane, string projectDir, string name, string projectFile)
        {
            string solution = FindSolution(projectDir, name);
            if (solution == null)
            {
                pane.WriteLine();
                await RunAsync(pane, projectDir, "new", "sln", "-n", name, "-o", projectDir);
                solution = FindSolution(projectDir, name);
                if (solution != null)
                {
                    await RunAsync(pane, projectDir, "sln", solution, "add", projectFile);
                }
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (Package.GetGlobalService(typeof(SVsSolution)) is IVsSolution vsSolution)
            {
                // Without a solution file the project itself opens (VS wraps it in one).
                vsSolution.OpenSolutionFile(0, solution ?? projectFile);
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
