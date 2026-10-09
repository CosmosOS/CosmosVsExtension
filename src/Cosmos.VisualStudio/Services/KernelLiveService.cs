using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Cosmos.VisualStudio.Core;
using EnvDTE;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace Cosmos.VisualStudio.Services
{
    /// <summary>State behind one live kernel view; raises Changed from any thread.</summary>
    internal abstract class LiveModel
    {
        private readonly string idleMessage;

        protected LiveModel(string idleMessage)
        {
            this.idleMessage = idleMessage;
            Message = idleMessage;
        }

        public string Message { get; private set; }

        public event EventHandler Changed;

        public void SetMessage(string message)
        {
            // Polling repeats the same state every second; only real changes repaint.
            if (message == Message && !HasData)
            {
                return;
            }
            Message = message;
            ClearData();
            Raise();
        }

        public void ResetToIdle() => SetMessage(idleMessage);

        protected void SetMessageKeepData(string message)
        {
            Message = message;
            Raise();
        }

        protected abstract void ClearData();

        protected abstract bool HasData { get; }

        protected void Raise() => Changed?.Invoke(this, EventArgs.Empty);

        public abstract string Serialize();
    }

    internal sealed class KernelThreadsModel : LiveModel
    {
        public KernelThreadsModel() : base("Start a Cosmos debug session to inspect kernel threads.") { }

        public IReadOnlyList<KernelThreadInfo> Threads { get; private set; } = Array.Empty<KernelThreadInfo>();

        public void Update(IReadOnlyList<KernelThreadInfo> threads, string message)
        {
            Threads = threads ?? Array.Empty<KernelThreadInfo>();
            SetMessageKeepData(message);
        }

        protected override void ClearData() => Threads = Array.Empty<KernelThreadInfo>();

        protected override bool HasData => Threads.Count > 0;

        public override string Serialize() => ThreadSnapshot.Serialize(Threads, Message);
    }

    internal sealed class KernelGCModel : LiveModel
    {
        public KernelGCModel() : base("Start a Cosmos debug session to inspect GC state.") { }

        public GCStats Stats { get; private set; }

        public void Update(GCStats stats, string message)
        {
            Stats = stats;
            SetMessageKeepData(message);
        }

        protected override void ClearData() => Stats = null;

        protected override bool HasData => Stats != null;

        public override string Serialize() => GCStats.Serialize(Stats, Message);
    }

    internal sealed class KernelMemoryModel : LiveModel
    {
        public KernelMemoryModel() : base("Start a Cosmos debug session to inspect memory manager state.") { }

        public MemoryStats Stats { get; private set; }
        public IReadOnlyList<PageExtent> Extents { get; private set; }
        public string ExtentsError { get; private set; }

        public void Update(MemoryStats stats, IReadOnlyList<PageExtent> extents, string extentsError, string message)
        {
            Stats = stats;
            Extents = extents;
            ExtentsError = extentsError;
            SetMessageKeepData(message);
        }

        public void ShowMessageIfEmpty(string message)
        {
            if (Stats == null)
            {
                SetMessageKeepData(message);
            }
        }

        protected override void ClearData()
        {
            Stats = null;
            Extents = null;
            ExtentsError = null;
        }

        protected override bool HasData => Stats != null;

        public override string Serialize() => MemoryStats.Serialize(Stats, Message);
    }

    /// <summary>
    /// Feeds the live kernel views. The kernel keeps snapshot buffers (threads,
    /// GC, memory manager) up to date from its timer tick; their addresses sit
    /// in static fields resolved from the ELF. They are read over QMP, which does
    /// not pause the guest, so the views stay current while the kernel runs.
    /// </summary>
    internal sealed class KernelLiveService
    {
        public static KernelLiveService Instance { get; } = new KernelLiveService();

        private readonly SemaphoreSlim pollGate = new SemaphoreSlim(1, 1);
        private QmpClient qmp;
        private CancellationTokenSource cts;
        private ulong? threadsStatics, gcStatics, memoryStatics;
        private ulong? threadsBuffer, gcBuffer, memoryBuffer;
        private bool loggedThreadsFallback;

        public KernelThreadsModel Threads { get; } = new KernelThreadsModel();
        public KernelGCModel GC { get; } = new KernelGCModel();
        public KernelMemoryModel Memory { get; } = new KernelMemoryModel();

        private static void Log(string message) => Panes.Output.WriteLine("[cosmos-debug] " + message);

        /// <summary>Starts polling a freshly connected QMP socket.</summary>
        public void Start(QmpClient client, IReadOnlyDictionary<string, ulong> statics)
        {
            Stop(null);
            qmp = client;
            threadsStatics = Lookup(statics, ThreadSnapshot.StaticsSymbol, "DebugLiveSnapshot", "falling back to gdb evaluation at the first break.");
            gcStatics = Lookup(statics, GCStats.StaticsSymbol, "DebugLiveGCSnapshot", "GC live view will be empty.");
            memoryStatics = Lookup(statics, MemoryStats.StaticsSymbol, "DebugLiveMemorySnapshot", "memory live view will be empty.");
            loggedThreadsFallback = false;

            Threads.SetMessage("Waiting for kernel snapshot…");
            GC.SetMessage("Waiting for GC snapshot…");
            Memory.SetMessage("Waiting for memory snapshot…");

            cts = new CancellationTokenSource();
            CancellationToken token = cts.Token;
            _ = Task.Run(() => LoopAsync(token));
        }

        /// <summary>Marks the session as without a live channel (QMP unavailable).</summary>
        public void StartWithoutQmp(string reason)
        {
            Stop(null);
            Threads.SetMessage(reason);
            GC.SetMessage(reason);
            Memory.SetMessage(reason);
        }

        private static ulong? Lookup(IReadOnlyDictionary<string, ulong> statics, string symbol, string label, string consequence)
        {
            if (statics != null && statics.TryGetValue(symbol, out ulong address))
            {
                Log($"{label} statics at 0x{address:x}");
                return address;
            }
            Log($"{label} symbol not found — {consequence}");
            return null;
        }

        /// <summary>Ends the session; <paramref name="message"/> replaces the views' content (null keeps it idle).</summary>
        public void Stop(string message)
        {
            cts?.Cancel();
            cts = null;
            qmp?.Dispose();
            qmp = null;
            threadsStatics = gcStatics = memoryStatics = null;
            threadsBuffer = gcBuffer = memoryBuffer = null;
            if (message != null)
            {
                Threads.SetMessage(message);
                GC.SetMessage(message);
                Memory.SetMessage(message);
            }
        }

        private async Task LoopAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(1500, token).ConfigureAwait(false);
                while (!token.IsCancellationRequested)
                {
                    await PollAsync().ConfigureAwait(false);
                    await Task.Delay(1000, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        /// <summary>One capture-or-poll pass over all views; skipped if one is already running.</summary>
        public async Task PollAsync()
        {
            if (!await pollGate.WaitAsync(0).ConfigureAwait(false))
            {
                return;
            }
            try
            {
                QmpClient client = qmp;
                if (client == null)
                {
                    return;
                }
                await PollThreadsAsync(client).ConfigureAwait(false);
                await PollGCAsync(client).ConfigureAwait(false);
                await PollMemoryAsync(client).ConfigureAwait(false);
            }
            finally
            {
                pollGate.Release();
            }
        }

        // Reads the 8-byte s_buffer pointer out of a snapshot class's statics block.
        private static async Task<ulong?> ReadPointerAsync(QmpClient client, ulong address)
        {
            byte[] bytes = await client.ReadVirtualAsync(address, 8).ConfigureAwait(false);
            return bytes.Length == 8 ? BitConverter.ToUInt64(bytes, 0) : (ulong?)null;
        }

        private async Task PollThreadsAsync(QmpClient client)
        {
            try
            {
                if (threadsBuffer == null)
                {
                    if (threadsStatics == null)
                    {
                        if (!loggedThreadsFallback)
                        {
                            loggedThreadsFallback = true;
                            Threads.SetMessage("Live snapshot address unknown — break into the debugger to locate it.");
                        }
                        return;
                    }
                    ulong? pointer = await ReadPointerAsync(client, threadsStatics.Value).ConfigureAwait(false);
                    if (pointer == null)
                    {
                        return;
                    }
                    if (pointer.Value == 0)
                    {
                        Threads.SetMessage("Live snapshot not initialized yet (scheduler not up).");
                        return;
                    }
                    threadsBuffer = pointer;
                    Log($"[kernel-threads] snapshot buffer at 0x{pointer.Value:x}");
                    Threads.SetMessage("Polling kernel snapshot…");
                }

                byte[] buffer = await client.ReadVirtualAsync(threadsBuffer.Value, ThreadSnapshot.Size).ConfigureAwait(false);
                List<KernelThreadInfo> parsed = ThreadSnapshot.Parse(buffer);
                if (parsed == null)
                {
                    Threads.SetMessage("Snapshot buffer not yet populated (bad magic).");
                }
                else
                {
                    Threads.Update(parsed, null);
                }
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                Log("[kernel-threads] poll error: " + e.Message);
            }
        }

        private async Task PollGCAsync(QmpClient client)
        {
            try
            {
                if (gcBuffer == null)
                {
                    if (gcStatics == null)
                    {
                        GC.SetMessage("GC snapshot symbol not found in the kernel ELF.");
                        return;
                    }
                    ulong? pointer = await ReadPointerAsync(client, gcStatics.Value).ConfigureAwait(false);
                    if (pointer == null)
                    {
                        return;
                    }
                    if (pointer.Value == 0)
                    {
                        GC.SetMessage("GC snapshot not initialized yet.");
                        return;
                    }
                    gcBuffer = pointer;
                    Log($"[kernel-gc] snapshot buffer at 0x{pointer.Value:x}");
                    GC.SetMessage("Polling GC snapshot…");
                }

                byte[] buffer = await client.ReadVirtualAsync(gcBuffer.Value, GCStats.Size).ConfigureAwait(false);
                GCStats stats = GCStats.Parse(buffer);
                if (stats == null)
                {
                    GC.SetMessage("GC snapshot buffer not yet populated.");
                }
                else
                {
                    GC.Update(stats, null);
                }
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                Log("[kernel-gc] poll error: " + e.Message);
            }
        }

        private async Task PollMemoryAsync(QmpClient client)
        {
            try
            {
                if (memoryBuffer == null)
                {
                    if (memoryStatics == null)
                    {
                        Memory.SetMessage("Memory snapshot symbol not found in the kernel ELF.");
                        return;
                    }
                    ulong? pointer = await ReadPointerAsync(client, memoryStatics.Value).ConfigureAwait(false);
                    if (pointer == null)
                    {
                        return;
                    }
                    if (pointer.Value == 0)
                    {
                        Memory.SetMessage("Memory snapshot not initialized yet.");
                        return;
                    }
                    memoryBuffer = pointer;
                    Log($"[kernel-memory] snapshot buffer at 0x{pointer.Value:x}");
                    Memory.SetMessage("Polling memory snapshot…");
                }

                // The kernel writes the snapshot from its timer tick, so a read can
                // land mid-update (odd seq). Retry briefly before giving up.
                MemoryStats stats = null;
                for (int attempt = 0; attempt < 3 && stats == null; attempt++)
                {
                    if (attempt > 0)
                    {
                        await Task.Delay(30).ConfigureAwait(false);
                    }
                    byte[] buffer = await client.ReadVirtualAsync(memoryBuffer.Value, MemoryStats.Size).ConfigureAwait(false);
                    stats = MemoryStats.Parse(buffer);
                }
                if (stats == null)
                {
                    // Keep the last good snapshot so the view doesn't flicker.
                    Memory.ShowMessageIfEmpty("Memory snapshot buffer not yet populated.");
                    return;
                }

                IReadOnlyList<PageExtent> extents = Memory.Extents;
                string extentsError = null;
                if (stats.Initialized && stats.RatAddress != 0 && stats.TotalPageCount > 0 && stats.TotalPageCount < int.MaxValue)
                {
                    int total = (int)stats.TotalPageCount;
                    try
                    {
                        byte[] rat = await client.ReadVirtualAsync(stats.RatAddress, total).ConfigureAwait(false);
                        if (rat.Length == total)
                        {
                            extents = MemoryStats.WalkExtents(rat);
                        }
                        else
                        {
                            extentsError = $"Short RAT read: got {rat.Length}/{total} bytes";
                        }
                    }
                    catch (Exception e) when (!(e is OutOfMemoryException))
                    {
                        extentsError = "RAT read failed: " + e.Message;
                        Log("[kernel-memory] " + extentsError);
                    }
                }
                Memory.Update(stats, extents, extentsError, null);
            }
            catch (Exception e) when (!(e is OutOfMemoryException))
            {
                Log("[kernel-memory] poll error: " + e.Message);
            }
        }

        /// <summary>
        /// Fallback for a kernel ELF without the threads statics symbol: once the
        /// debugger breaks, ask the kernel for its snapshot address directly.
        /// </summary>
        public void OnDebuggerBreak()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (threadsBuffer != null || threadsStatics != null || qmp == null)
            {
                return;
            }
            try
            {
                if (!(Package.GetGlobalService(typeof(SDTE)) is DTE dte))
                {
                    return;
                }
                Expression expression = dte.Debugger.GetExpression("(unsigned long long)CosmosDbg_GetSnapshotAddr()", true, 2000);
                string raw = expression?.IsValidValue == true ? expression.Value : null;
                Log("[kernel-threads] capture raw=" + (raw ?? "<invalid>"));
                Match m = raw == null ? Match.Empty : Regex.Match(raw, "0x[0-9a-fA-F]+|\\d+");
                if (!m.Success)
                {
                    Threads.SetMessage("Could not locate live snapshot buffer (parse failed).");
                    return;
                }
                ulong address = m.Value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? ulong.Parse(m.Value.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                    : ulong.Parse(m.Value, CultureInfo.InvariantCulture);
                if (address == 0)
                {
                    Threads.SetMessage("Live snapshot not initialized yet — continue or hit another breakpoint after the scheduler starts.");
                    return;
                }
                threadsBuffer = address;
                Log($"[kernel-threads] snapshot buffer at 0x{address:x}");
                Threads.SetMessage("Polling kernel snapshot…");
            }
            catch (Exception e) when (e is System.Runtime.InteropServices.COMException || e is FormatException || e is OverflowException)
            {
                Threads.SetMessage("Snapshot capture failed: " + e.Message);
            }
        }
    }
}
