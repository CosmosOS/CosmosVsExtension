using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using System.Windows;
using Cosmos.VisualStudio.Core;
using Cosmos.VisualStudio.Services;
using Cosmos.VisualStudio.ToolWindows;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace Cosmos.VisualStudio
{
    /// <summary>Wires the command table (CosmosPackage.vsct) to the services.</summary>
    internal static class CosmosCommands
    {
        public static async Task InitializeAsync(CosmosPackage package)
        {
            var commands = (OleMenuCommandService)await package.GetServiceAsync(typeof(IMenuCommandService));
            await package.JoinableTaskFactory.SwitchToMainThreadAsync();

            Add(commands, PackageIds.NewProject, () => package.NewProject.CreateAsync());

            // Cosmos menu: act on the startup kernel (or the first kernel found).
            AddKernel(commands, package, PackageIds.Build, contextual: false,
                p => package.Builder.BuildAsync(p.Csproj, p.Arch, KernelBuilder.ActiveConfiguration()));
            AddKernel(commands, package, PackageIds.Run, contextual: false, p => package.Launcher.RunAsync(package.TargetFor(p)));
            AddKernel(commands, package, PackageIds.Debug, contextual: false, p => package.Launcher.DebugAsync(package.TargetFor(p)));
            AddKernel(commands, package, PackageIds.Clean, contextual: false, p => { package.Builder.Clean(p); return Task.CompletedTask; });
            AddKernel(commands, package, PackageIds.Properties, contextual: false, p => ShowPropertiesAsync(package, p));

            // Solution Explorer: act on the right-clicked kernel project.
            AddKernel(commands, package, PackageIds.CtxBuild, contextual: true,
                p => package.Builder.BuildAsync(p.Csproj, p.Arch, KernelBuilder.ActiveConfiguration()));
            AddKernel(commands, package, PackageIds.CtxRun, contextual: true, p => package.Launcher.RunAsync(package.TargetFor(p)));
            AddKernel(commands, package, PackageIds.CtxDebug, contextual: true, p => package.Launcher.DebugAsync(package.TargetFor(p)));
            AddKernel(commands, package, PackageIds.CtxClean, contextual: true, p => { package.Builder.Clean(p); return Task.CompletedTask; });

            OleMenuCommand stop = Add(commands, PackageIds.Stop, () =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                package.Builder.Cancel();
                package.Launcher.Stop();
            });
            stop.BeforeQueryStatus += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                stop.Enabled = package.Builder.IsBuilding || package.Launcher.IsRunning || package.Launcher.IsDebugging;
            };

            Add(commands, PackageIds.CheckTools, () => package.Tools.CheckAsync());
            Add(commands, PackageIds.InstallTools, () => package.Tools.Install());

            Add(commands, PackageIds.ShowExplorer, () => package.ShowToolWindowAsync<CosmosExplorerWindow>());
            Add(commands, PackageIds.ShowTests, () => package.ShowToolWindowAsync<KernelTestsWindow>());
            Add(commands, PackageIds.ShowThreads, () => package.ShowToolWindowAsync<KernelThreadsWindow>());
            Add(commands, PackageIds.ShowGC, () => package.ShowToolWindowAsync<KernelGCWindow>());
            Add(commands, PackageIds.ShowMemory, () => package.ShowToolWindowAsync<KernelMemoryWindow>());
            Add(commands, PackageIds.ShowMemoryMap, () => package.ShowToolWindowAsync<KernelMemoryMapWindow>());

            KernelLiveService live = KernelLiveService.Instance;
            Add(commands, PackageIds.ThreadsRefresh, live.PollAsync);
            Add(commands, PackageIds.GCRefresh, live.PollAsync);
            Add(commands, PackageIds.MemoryRefresh, live.PollAsync);
            Add(commands, PackageIds.ThreadsCopy, () => CopyAsync(live.Threads, "Kernel Threads"));
            Add(commands, PackageIds.GCCopy, () => CopyAsync(live.GC, "Kernel GC"));
            Add(commands, PackageIds.MemoryCopy, () => CopyAsync(live.Memory, "Kernel Memory"));
        }

        private static OleMenuCommand Add(OleMenuCommandService commands, int id, Action handler) =>
            Add(commands, id, () =>
            {
                handler();
                return Task.CompletedTask;
            });

        private static OleMenuCommand Add(OleMenuCommandService commands, int id, Func<Task> handler)
        {
            var command = new OleMenuCommand((s, e) =>
            {
                CosmosPackage.Instance.JoinableTaskFactory.RunAsync(async () =>
                {
                    await CosmosPackage.Instance.JoinableTaskFactory.SwitchToMainThreadAsync();
                    await handler();
                }).FileAndForget("Cosmos/Command/" + id);
            }, new CommandID(PackageGuids.CommandSet, id));
            commands.AddCommand(command);
            return command;
        }

        private static void AddKernel(OleMenuCommandService commands, CosmosPackage package, int id, bool contextual, Func<ProjectInfo, Task> handler)
        {
            OleMenuCommand command = Add(commands, id, async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                ProjectInfo project = contextual ? package.Workspace.FindSelectedProject() : package.Workspace.FindProject();
                if (project == null)
                {
                    package.ShowMessage("No Cosmos project found. Create one with Cosmos > New Kernel Project, or open a kernel project.", OLEMSGICON.OLEMSGICON_WARNING);
                    return;
                }
                await handler(project);
            });
            command.BeforeQueryStatus += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (contextual)
                {
                    bool kernel = package.Workspace.IsCosmosProject(package.Workspace.SelectedProjectPath);
                    command.Visible = kernel;
                    command.Enabled = kernel && !package.Builder.IsBuilding;
                }
                else
                {
                    command.Enabled = package.Workspace.CurrentProject != null && !package.Builder.IsBuilding;
                }
            };
        }

        /// <summary>
        /// A kernel loaded as a project edits its properties in Visual Studio's
        /// Project Properties, where the Cosmos and QEMU pages are (the
        /// project's own Properties command opens the same). An open folder has
        /// no project system behind it, so there the Cosmos window stands in.
        /// </summary>
        private static async Task ShowPropertiesAsync(CosmosPackage package, ProjectInfo project)
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (package.Workspace.OpenProjectProperties(project.Csproj))
            {
                return;
            }
            KernelPropertiesWindow window = await package.ShowToolWindowAsync<KernelPropertiesWindow>();
            await package.JoinableTaskFactory.SwitchToMainThreadAsync();
            window.Load(project);
        }

        private static async Task CopyAsync(LiveModel model, string name)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            try
            {
                Clipboard.SetText(model.Serialize());
                StatusBar.SetText(name + " copied to clipboard");
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                StatusBar.SetText("The clipboard is busy; try again.");
            }
        }
    }
}
