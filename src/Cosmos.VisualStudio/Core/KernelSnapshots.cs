using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Cosmos.VisualStudio.Core
{
    // Snapshot layouts — keep in sync with the kernel's DebugLiveSnapshot.cs,
    // DebugLiveGCSnapshot.cs, DebugLiveMemorySnapshot.cs and PageType.cs.
    // Every snapshot starts with a magic and a seqlock counter at offset 16:
    // an odd counter means the kernel was mid-update, so the read is dropped.

    public sealed class KernelThreadInfo
    {
        public int Slot { get; set; }
        public uint Id { get; set; }
        public string State { get; set; } = "";
        public int CpuId { get; set; }
        public uint Flags { get; set; }

        public string FlagNames
        {
            get
            {
                var bits = new List<string>();
                if ((Flags & 0x1) != 0) bits.Add("Kernel");
                if ((Flags & 0x2) != 0) bits.Add("Idle");
                if ((Flags & 0x4) != 0) bits.Add("Pinned");
                if ((Flags & 0x8) != 0) bits.Add("Managed");
                return string.Join("|", bits);
            }
        }

        public string CpuText => CpuId >= 0 ? CpuId.ToString(CultureInfo.InvariantCulture) : "";
    }

    public static class ThreadSnapshot
    {
        public const uint Magic = 0xC05D0001;
        public const int HeaderSize = 24;
        public const int EntrySize = 16;
        public const int MaxEntries = 64;
        public const int Size = HeaderSize + MaxEntries * EntrySize;
        public const string StaticsSymbol = "__NONGCSTATICSCosmos_Kernel_Core_Cosmos_Kernel_Core_Runtime_DebugLiveSnapshot";

        private static readonly string[] StateNames = { "Created", "Ready", "Running", "Blocked", "Sleeping", "Dead" };

        /// <summary>The threads in the buffer, or null on a bad magic or a torn (odd seq) read.</summary>
        public static List<KernelThreadInfo> Parse(byte[] buf)
        {
            if (buf == null || buf.Length < HeaderSize || BitConverter.ToUInt32(buf, 0) != Magic)
            {
                return null;
            }
            uint count = BitConverter.ToUInt32(buf, 8);
            if ((BitConverter.ToUInt64(buf, 16) & 1) != 0)
            {
                return null;
            }
            var entries = new List<KernelThreadInfo>();
            int n = (int)Math.Min(count, MaxEntries);
            for (int i = 0; i < n; i++)
            {
                int off = HeaderSize + i * EntrySize;
                if (off + EntrySize > buf.Length)
                {
                    break;
                }
                uint state = BitConverter.ToUInt32(buf, off + 8);
                entries.Add(new KernelThreadInfo
                {
                    Slot = i,
                    Id = BitConverter.ToUInt32(buf, off),
                    CpuId = BitConverter.ToInt32(buf, off + 4),
                    State = state < StateNames.Length ? StateNames[state] : "?" + state,
                    Flags = BitConverter.ToUInt32(buf, off + 12)
                });
            }
            return entries;
        }

        public static string Serialize(IReadOnlyList<KernelThreadInfo> threads, string message)
        {
            var lines = new List<string>();
            if (message != null)
            {
                lines.Add(message);
            }
            if ((threads == null || threads.Count == 0) && message == null)
            {
                lines.Add("(empty)");
            }
            foreach (KernelThreadInfo t in threads ?? Array.Empty<KernelThreadInfo>())
            {
                string cpu = t.CpuId >= 0 ? " on CPU " + t.CpuId : "";
                lines.Add($"slot {t.Slot}: #{t.Id} [{t.State}]{cpu} flags=0x{t.Flags:x}");
            }
            return string.Join(Environment.NewLine, lines);
        }
    }

    public sealed class GCStats
    {
        public bool Initialized { get; set; }
        public ulong HeapSizeBytes { get; set; }
        public ulong FragmentedBytes { get; set; }
        public ulong TotalCommittedBytes { get; set; }
        public ulong TotalAllocatedBytes { get; set; }
        public ulong PinnedObjectsCount { get; set; }
        public uint CollectionCount { get; set; }
        public uint TotalObjectsFreed { get; set; }
        public ulong MemoryLoadBytes { get; set; }
        public ulong GcSegmentSize { get; set; }
        public uint LastGCPercentTimeInGC { get; set; }
        public ulong LastGen0SizeBefore { get; set; }
        public ulong LastGen0FragBefore { get; set; }
        public ulong LastGen0SizeAfter { get; set; }
        public ulong LastGen0FragAfter { get; set; }

        public const uint Magic = 0xC05D0002;
        public const int Size = 160;
        public const string StaticsSymbol = "__NONGCSTATICSCosmos_Kernel_Core_Cosmos_Kernel_Core_Runtime_DebugLiveGCSnapshot";

        public static GCStats Parse(byte[] buf)
        {
            if (buf == null || buf.Length < 136 || BitConverter.ToUInt32(buf, 0) != Magic)
            {
                return null;
            }
            if ((BitConverter.ToUInt64(buf, 16) & 1) != 0)
            {
                return null;
            }
            return new GCStats
            {
                Initialized = (BitConverter.ToUInt32(buf, 8) & 1) != 0,
                HeapSizeBytes = BitConverter.ToUInt64(buf, 24),
                FragmentedBytes = BitConverter.ToUInt64(buf, 32),
                TotalCommittedBytes = BitConverter.ToUInt64(buf, 40),
                TotalAllocatedBytes = BitConverter.ToUInt64(buf, 48),
                PinnedObjectsCount = BitConverter.ToUInt64(buf, 56),
                CollectionCount = BitConverter.ToUInt32(buf, 64),
                TotalObjectsFreed = BitConverter.ToUInt32(buf, 68),
                MemoryLoadBytes = BitConverter.ToUInt64(buf, 72),
                GcSegmentSize = BitConverter.ToUInt64(buf, 80),
                LastGCPercentTimeInGC = BitConverter.ToUInt32(buf, 96),
                LastGen0SizeBefore = BitConverter.ToUInt64(buf, 104),
                LastGen0FragBefore = BitConverter.ToUInt64(buf, 112),
                LastGen0SizeAfter = BitConverter.ToUInt64(buf, 120),
                LastGen0FragAfter = BitConverter.ToUInt64(buf, 128)
            };
        }

        /// <summary>Label / value rows, in display order.</summary>
        public IReadOnlyList<KeyValuePair<string, string>> Rows()
        {
            return new List<KeyValuePair<string, string>>
            {
                Row("Status", Initialized ? "initialized" : "not initialized"),
                Row("Heap size", Format.Bytes(HeapSizeBytes)),
                Row("Fragmented", Format.Bytes(FragmentedBytes)),
                Row("Total committed", Format.Bytes(TotalCommittedBytes)),
                Row("Total allocated", Format.Bytes(TotalAllocatedBytes)),
                Row("Pinned objects", PinnedObjectsCount.ToString(CultureInfo.InvariantCulture)),
                Row("Collections", CollectionCount.ToString(CultureInfo.InvariantCulture)),
                Row("Objects freed", TotalObjectsFreed.ToString(CultureInfo.InvariantCulture)),
                Row("Memory load", Format.Bytes(MemoryLoadBytes)),
                Row("Segment size", Format.Bytes(GcSegmentSize)),
                Row("Last GC %time-in-GC", LastGCPercentTimeInGC + "%"),
                Row("Last gen0 size (before)", Format.Bytes(LastGen0SizeBefore)),
                Row("Last gen0 frag (before)", Format.Bytes(LastGen0FragBefore)),
                Row("Last gen0 size (after)", Format.Bytes(LastGen0SizeAfter)),
                Row("Last gen0 frag (after)", Format.Bytes(LastGen0FragAfter))
            };
        }

        public static string Serialize(GCStats s, string message)
        {
            var lines = new List<string>();
            if (message != null)
            {
                lines.Add(message);
            }
            if (s == null)
            {
                if (message == null) lines.Add("(no data)");
                return string.Join(Environment.NewLine, lines);
            }
            lines.Add($"Initialized:           {(s.Initialized ? "true" : "false")}");
            lines.Add($"Heap size:             {Format.Bytes(s.HeapSizeBytes)}");
            lines.Add($"Fragmented:            {Format.Bytes(s.FragmentedBytes)}");
            lines.Add($"Total committed:       {Format.Bytes(s.TotalCommittedBytes)}");
            lines.Add($"Total allocated:       {Format.Bytes(s.TotalAllocatedBytes)}");
            lines.Add($"Pinned objects:        {s.PinnedObjectsCount}");
            lines.Add($"Collections:           {s.CollectionCount}");
            lines.Add($"Objects freed (total): {s.TotalObjectsFreed}");
            lines.Add($"Memory load:           {Format.Bytes(s.MemoryLoadBytes)}");
            lines.Add($"Segment size:          {Format.Bytes(s.GcSegmentSize)}");
            lines.Add($"Last GC %time-in-GC:   {s.LastGCPercentTimeInGC}%");
            lines.Add($"Last gen0 before:      size={Format.Bytes(s.LastGen0SizeBefore)} frag={Format.Bytes(s.LastGen0FragBefore)}");
            lines.Add($"Last gen0 after:       size={Format.Bytes(s.LastGen0SizeAfter)} frag={Format.Bytes(s.LastGen0FragAfter)}");
            return string.Join(Environment.NewLine, lines);
        }

        private static KeyValuePair<string, string> Row(string label, string value) => new KeyValuePair<string, string>(label, value);
    }

    /// <summary>A PageType byte of the kernel's Region Allocation Table.</summary>
    public sealed class PageTypeInfo
    {
        public PageTypeInfo(byte id, string name, uint color)
        {
            Id = id;
            Name = name;
            Color = color;
        }

        public byte Id { get; }
        public string Name { get; }
        // 0xRRGGBB
        public uint Color { get; }
    }

    public static class PageTypes
    {
        public const byte Empty = 0;
        public const byte Extension = 128;

        public static IReadOnlyList<PageTypeInfo> All { get; } = new[]
        {
            new PageTypeInfo(0, "Empty", 0x1e2229),
            new PageTypeInfo(1, "GCHeap", 0x4ec9b0),
            new PageTypeInfo(3, "HeapSmall", 0x569cd6),
            new PageTypeInfo(5, "HeapMedium", 0xe6a32e),
            new PageTypeInfo(7, "HeapLarge", 0xa374d5),
            new PageTypeInfo(9, "Unmanaged", 0xce9178),
            new PageTypeInfo(11, "PageDirectory", 0xdcdcaa),
            new PageTypeInfo(32, "PageAllocator", 0xb5cea8),
            new PageTypeInfo(64, "SMT", 0xf48771),
            new PageTypeInfo(128, "Extension", 0x6e6e6e)
        };

        public const uint UnknownColor = 0xff0066;

        public static PageTypeInfo Find(int id) => All.FirstOrDefault(t => t.Id == id);

        public static string NameOf(int id) => Find(id)?.Name ?? "Type=" + id;
    }

    public struct PageExtent
    {
        public PageExtent(int start, int length, byte type)
        {
            Start = start;
            Length = length;
            Type = type;
        }

        /// <summary>First RAT index (also the first page index relative to RamStart).</summary>
        public int Start { get; }
        /// <summary>Pages in this extent, coalesced Extension pages included.</summary>
        public int Length { get; }
        /// <summary>Owner PageType; Extension is coalesced into the owner.</summary>
        public byte Type { get; }
    }

    public sealed class MemoryStats
    {
        public bool Initialized { get; set; }
        public uint PageSize { get; set; }
        public ulong RamStart { get; set; }
        public ulong RamSize { get; set; }
        public ulong TotalPageCount { get; set; }
        public ulong FreePageCount { get; set; }
        public ulong RatAddress { get; set; }
        public ulong HeapEnd { get; set; }
        public ulong PagesEmpty { get; set; }
        public ulong PagesGCHeap { get; set; }
        public ulong PagesHeapSmall { get; set; }
        public ulong PagesHeapMedium { get; set; }
        public ulong PagesHeapLarge { get; set; }
        public ulong PagesUnmanaged { get; set; }
        public ulong PagesPageDirectory { get; set; }
        public ulong PagesPageAllocator { get; set; }
        public ulong PagesSMT { get; set; }
        public ulong PagesExtension { get; set; }
        public ulong PagesUnknown { get; set; }

        public const uint Magic = 0xC05D0003;
        public const int Size = 192;
        public const string StaticsSymbol = "__NONGCSTATICSCosmos_Kernel_Core_Cosmos_Kernel_Core_Runtime_DebugLiveMemorySnapshot";

        public ulong UsedPageCount => TotalPageCount >= FreePageCount ? TotalPageCount - FreePageCount : 0;

        public ulong PagesAsBytes(ulong pages) => pages * PageSize;

        public static MemoryStats Parse(byte[] buf)
        {
            if (buf == null || buf.Length < 160 || BitConverter.ToUInt32(buf, 0) != Magic)
            {
                return null;
            }
            if ((BitConverter.ToUInt64(buf, 16) & 1) != 0)
            {
                return null;
            }
            return new MemoryStats
            {
                Initialized = (BitConverter.ToUInt32(buf, 8) & 1) != 0,
                PageSize = BitConverter.ToUInt32(buf, 12),
                RamStart = BitConverter.ToUInt64(buf, 24),
                RamSize = BitConverter.ToUInt64(buf, 32),
                TotalPageCount = BitConverter.ToUInt64(buf, 40),
                FreePageCount = BitConverter.ToUInt64(buf, 48),
                RatAddress = BitConverter.ToUInt64(buf, 56),
                HeapEnd = BitConverter.ToUInt64(buf, 64),
                PagesEmpty = BitConverter.ToUInt64(buf, 72),
                PagesGCHeap = BitConverter.ToUInt64(buf, 80),
                PagesHeapSmall = BitConverter.ToUInt64(buf, 88),
                PagesHeapMedium = BitConverter.ToUInt64(buf, 96),
                PagesHeapLarge = BitConverter.ToUInt64(buf, 104),
                PagesUnmanaged = BitConverter.ToUInt64(buf, 112),
                PagesPageDirectory = BitConverter.ToUInt64(buf, 120),
                PagesPageAllocator = BitConverter.ToUInt64(buf, 128),
                PagesSMT = BitConverter.ToUInt64(buf, 136),
                PagesExtension = BitConverter.ToUInt64(buf, 144),
                PagesUnknown = BitConverter.ToUInt64(buf, 152)
            };
        }

        /// <summary>Page counts keyed by PageType id, as the memory map legend shows them.</summary>
        public IReadOnlyDictionary<byte, ulong> TypeCounts => new Dictionary<byte, ulong>
        {
            [0] = PagesEmpty,
            [1] = PagesGCHeap,
            [3] = PagesHeapSmall,
            [5] = PagesHeapMedium,
            [7] = PagesHeapLarge,
            [9] = PagesUnmanaged,
            [11] = PagesPageDirectory,
            [32] = PagesPageAllocator,
            [64] = PagesSMT,
            [128] = PagesExtension
        };

        /// <summary>Label / value / tooltip rows of the Kernel Memory window, in display order.</summary>
        public IReadOnlyList<string[]> Rows()
        {
            ulong used = UsedPageCount;
            return new List<string[]>
            {
                new[] { "Status", Initialized ? "initialized" : "not initialized", null },
                new[] { "Page size", PageSize + " B", null },
                new[] { "RAM start", Format.Hex(RamStart), "Base of the heap data area." },
                new[] { "RAM size", Format.Bytes(RamSize), "Heap data area only (RAT pages excluded)." },
                new[] { "RAT address", Format.Hex(RatAddress), "Region Allocation Table base. By design the RAT sits at the end of the heap, so this is also where the heap ends." },
                new[] { "Total pages", TotalPageCount.ToString(CultureInfo.InvariantCulture), null },
                new[] { "Free pages", $"{FreePageCount} ({Format.Bytes(PagesAsBytes(FreePageCount))})", null },
                new[] { "Used pages", $"{used} ({Format.Bytes(PagesAsBytes(used))})", null }
            };
        }

        public static string Serialize(MemoryStats s, string message)
        {
            var lines = new List<string>();
            if (message != null)
            {
                lines.Add(message);
            }
            if (s == null)
            {
                if (message == null) lines.Add("(no data)");
                return string.Join(Environment.NewLine, lines);
            }
            ulong used = s.UsedPageCount;
            string Pages(ulong n) => $"{n} ({Format.Bytes(s.PagesAsBytes(n))})";
            lines.Add($"Initialized:        {(s.Initialized ? "true" : "false")}");
            lines.Add($"Page size:          {s.PageSize} B");
            lines.Add($"RAM start:          {Format.Hex(s.RamStart)}");
            lines.Add($"RAM size:           {Format.Bytes(s.RamSize)}");
            lines.Add($"Heap end:           {Format.Hex(s.HeapEnd)}");
            lines.Add($"RAT address:        {Format.Hex(s.RatAddress)}");
            lines.Add($"Total pages:        {s.TotalPageCount}");
            lines.Add($"Free pages:         {Pages(s.FreePageCount)}");
            lines.Add($"Used pages:         {Pages(used)}");
            lines.Add("Page composition:");
            lines.Add($"  Empty:            {Pages(s.PagesEmpty)}");
            lines.Add($"  GCHeap:           {Pages(s.PagesGCHeap)}");
            lines.Add($"  HeapSmall:        {Pages(s.PagesHeapSmall)}");
            lines.Add($"  HeapMedium:       {Pages(s.PagesHeapMedium)}");
            lines.Add($"  HeapLarge:        {Pages(s.PagesHeapLarge)}");
            lines.Add($"  Unmanaged:        {Pages(s.PagesUnmanaged)}");
            lines.Add($"  PageDirectory:    {Pages(s.PagesPageDirectory)}");
            lines.Add($"  PageAllocator:    {Pages(s.PagesPageAllocator)}");
            lines.Add($"  SMT:              {Pages(s.PagesSMT)}");
            lines.Add($"  Extension:        {Pages(s.PagesExtension)}");
            if (s.PagesUnknown > 0)
            {
                lines.Add($"  Unknown:          {s.PagesUnknown}");
            }
            return string.Join(Environment.NewLine, lines);
        }

        /// <summary>
        /// Walks raw RAT bytes into coalesced extents. Each non-Extension byte
        /// starts an extent and the Extension bytes after it fold in; Empty pages
        /// coalesce with adjacent Empty pages. Orphan Extension runs become their
        /// own extents so a corrupted RAT shows up instead of vanishing.
        /// </summary>
        public static List<PageExtent> WalkExtents(byte[] rat)
        {
            var output = new List<PageExtent>();
            int n = rat.Length;
            int i = 0;
            while (i < n)
            {
                byte t = rat[i];
                int start = i;
                if (t == PageTypes.Extension)
                {
                    while (i < n && rat[i] == PageTypes.Extension) i++;
                    output.Add(new PageExtent(start, i - start, PageTypes.Extension));
                    continue;
                }
                i++;
                byte follow = t == PageTypes.Empty ? PageTypes.Empty : PageTypes.Extension;
                while (i < n && rat[i] == follow) i++;
                output.Add(new PageExtent(start, i - start, t));
            }
            return output;
        }
    }

    public static class Format
    {
        public static string Bytes(ulong n)
        {
            if (n < 1024)
            {
                return n.ToString(CultureInfo.InvariantCulture) + " B";
            }
            double kb = n / 1024.0;
            if (kb < 1024)
            {
                return kb.ToString("0.0", CultureInfo.InvariantCulture) + " KiB";
            }
            double mb = kb / 1024;
            if (mb < 1024)
            {
                return mb.ToString("0.00", CultureInfo.InvariantCulture) + " MiB";
            }
            return (mb / 1024).ToString("0.00", CultureInfo.InvariantCulture) + " GiB";
        }

        public static string Hex(ulong n) => "0x" + n.ToString("x16", CultureInfo.InvariantCulture);
    }
}
