# Cosmos OS for Visual Studio

Visual Studio extension for [Cosmos gen3](https://github.com/valentinbreiz/nativeaot-patcher), the NativeAOT-based C# kernel framework. Create, build, run and debug a kernel without leaving the IDE.

## Features

Available from the **Cosmos** menu (under Extensions), the **Cosmos OS** tool window, the project's context menu in Solution Explorer, and F5 / Ctrl+F5:

- New kernel project from the `cosmos-kernel` template, in a new solution or added to the open one
- Build the kernel through `cosmos build`, with errors and warnings in the Error List
- Run it in QEMU through `cosmos run`, the serial console streaming into the Output window
- Debug with GDB through Visual Studio's debugger
- Live kernel diagnostics while debugging: threads, GC, memory and a page map
- Edit project properties in Visual Studio's own Project Properties: **Cosmos** and **QEMU** pages for the target architecture, kernel features, QEMU machine, devices (network card, keyboard, mouse, audio), disks and port forwards
- Run the Cosmos kernel test suites (in the Cosmos repository)
- Check and install the toolchain (.NET 10 SDK, Cosmos CLI, QEMU, GDB)
- Clean build outputs

## Requirements

- Visual Studio 2026 (or 2022 17.14+), Community edition or higher
- The **Linux and embedded development with C++** workload, for debugging (it provides the GDB debug engine)
- [.NET SDK 10.0](https://dotnet.microsoft.com/download)+
- [Cosmos.Tools](https://www.nuget.org/packages/Cosmos.Tools) CLI, QEMU, and GDB (`gdb-multiarch` for ARM64)

Missing tools can be installed from the extension.

## Installation

Install the `.vsix` from the [releases page](https://github.com/CosmosOS/CosmosVsExtension/releases), then set up the toolchain:

```bash
dotnet tool install -g Cosmos.Tools
cosmos install
cosmos check
```

On Windows, you can run `CosmosSetup-<version>-windows.exe` from the [nativeaot-patcher releases](https://github.com/valentinbreiz/nativeaot-patcher/releases) instead.

## Running and debugging

When the startup project is a Cosmos kernel, **Start Debugging** (F5) boots it in QEMU and attaches GDB to QEMU's gdbstub, and **Start Without Debugging** (Ctrl+F5) runs it. Both build first when the sources changed since the last build. The same actions are in the **Cosmos** menu and on the project's context menu.

- The serial console streams into the **Cosmos OS - Output** pane.
- While debugging, **Cosmos Kernel Threads**, **Cosmos Kernel GC**, **Cosmos Kernel Memory** and **Cosmos Kernel Memory Map** open next to the Output window (also under **Debug > Windows**). They are read over QEMU's QMP socket without pausing the guest.
- The NativeAOT pretty-printers render strings and arrays when GDB has Python.

**Stop Debugging** ends QEMU and GDB together. QEMU's devices, memory and disks come from the project's properties.

Settings live under **Tools > Options > Cosmos OS**: default architecture for new projects, whether F5 / Ctrl+F5 are handled, build before running, and the test mode.

## Project properties

**Properties** on a kernel project (or **Cosmos > Kernel Properties**) opens Visual Studio's Project Properties, with two pages ahead of the standard ones:

- **Cosmos**: target architecture, kernel entry class, kernel features (`CosmosEnable*`) and C compiler flags. Features that depend on a disabled one are hidden, as the SDK turns them off too.
- **QEMU**: memory, machine type, CPU, serial output, network card, port forwards, keyboard, mouse, audio, disks and extra arguments. Only devices with a kernel driver for the target architecture are listed.

Kernel features and flags are written to the `.csproj`; the architecture and QEMU settings to `.cosmos/config.json`, shared with the VS Code extension. Disks are one per line: the image path, then `nvme` for an NVMe controller and the size to create a missing image with (`data.img nvme 1G`).

A kernel opened as a folder has no Project Properties; there **Kernel Properties** opens the Cosmos properties window instead.

## Testing

In the Cosmos repository, **Cosmos > Kernel Tests** lists the suites under `tests/Kernels`. Run them on x64 or arm64, or debug a suite's kernel under GDB. Output goes to the **Cosmos OS - Tests** pane.

## Building from source

Open `CosmosVsExtension.slnx` in Visual Studio with the **Visual Studio extension development** workload, set `Cosmos.VisualStudio` as the startup project and press F5: an experimental instance starts with the extension loaded. The `.vsix` lands in `src/Cosmos.VisualStudio/bin/<Configuration>/net472/`.

From the command line:

```bash
msbuild src/Cosmos.VisualStudio/Cosmos.VisualStudio.csproj /restore /p:Configuration=Release
dotnet test tests/Cosmos.VisualStudio.Tests
```

The unit tests cover the IDE-independent code under `src/Cosmos.VisualStudio/Core` and run on any OS.

## Documentation

[User Guide](https://valentinbreiz.github.io/nativeaot-patcher/articles/user/install.html) — installation, kernel startup, filesystem, network, graphics and debugging.

Also available for [VS Code](https://github.com/CosmosOS/CosmosVsCodeExtension) and [Rider](https://github.com/CosmosOS/CosmosRiderExtension).

## License

MIT
