using System;
using System.Linq;
using Cosmos.VisualStudio.Core;
using Xunit;

namespace Cosmos.VisualStudio.Tests
{
    public class SnapshotTests
    {
        private static void Put32(byte[] b, int off, uint v) => BitConverter.GetBytes(v).CopyTo(b, off);
        private static void Put64(byte[] b, int off, ulong v) => BitConverter.GetBytes(v).CopyTo(b, off);

        [Fact]
        public void ThreadSnapshotParsesEntries()
        {
            var buf = new byte[ThreadSnapshot.Size];
            Put32(buf, 0, ThreadSnapshot.Magic);
            Put32(buf, 8, 2);
            Put64(buf, 16, 4);
            // slot 0: id 1, cpu 0, Running, Kernel|Idle
            Put32(buf, 24, 1); Put32(buf, 28, 0); Put32(buf, 32, 2); Put32(buf, 36, 0x3);
            // slot 1: id 7, no cpu, Sleeping, Managed
            Put32(buf, 40, 7); Put32(buf, 44, 0xFFFFFFFF); Put32(buf, 48, 4); Put32(buf, 52, 0x8);

            var threads = ThreadSnapshot.Parse(buf);

            Assert.NotNull(threads);
            Assert.Equal(2, threads!.Count);
            Assert.Equal("Running", threads[0].State);
            Assert.Equal("Kernel|Idle", threads[0].FlagNames);
            Assert.Equal(-1, threads[1].CpuId);
            Assert.Equal("Sleeping", threads[1].State);
            Assert.Equal(
                "slot 0: #1 [Running] on CPU 0 flags=0x3" + Environment.NewLine + "slot 1: #7 [Sleeping] flags=0x8",
                ThreadSnapshot.Serialize(threads, null));
        }

        [Fact]
        public void TornOrForeignBuffersAreRejected()
        {
            var buf = new byte[ThreadSnapshot.Size];
            Assert.Null(ThreadSnapshot.Parse(buf));
            Put32(buf, 0, ThreadSnapshot.Magic);
            Put64(buf, 16, 3);
            Assert.Null(ThreadSnapshot.Parse(buf));

            var gc = new byte[GCStats.Size];
            Put32(gc, 0, GCStats.Magic);
            Put64(gc, 16, 1);
            Assert.Null(GCStats.Parse(gc));
        }

        [Fact]
        public void GCStatsParse()
        {
            var buf = new byte[GCStats.Size];
            Put32(buf, 0, GCStats.Magic);
            Put32(buf, 8, 1);
            Put64(buf, 24, 3 * 1024 * 1024);
            Put32(buf, 64, 12);
            Put32(buf, 96, 5);
            Put64(buf, 128, 2048);

            GCStats s = GCStats.Parse(buf)!;

            Assert.True(s.Initialized);
            Assert.Equal("3.00 MiB", Format.Bytes(s.HeapSizeBytes));
            Assert.Equal(12u, s.CollectionCount);
            Assert.Equal("5%", s.Rows().Single(r => r.Key == "Last GC %time-in-GC").Value);
            Assert.Equal("2.0 KiB", s.Rows().Last().Value);
        }

        [Fact]
        public void MemoryStatsParse()
        {
            var buf = new byte[MemoryStats.Size];
            Put32(buf, 0, MemoryStats.Magic);
            Put32(buf, 8, 1);
            Put32(buf, 12, 4096);
            Put64(buf, 24, 0xffff800000100000);
            Put64(buf, 40, 100);
            Put64(buf, 48, 40);
            Put64(buf, 80, 10);

            MemoryStats s = MemoryStats.Parse(buf)!;

            Assert.Equal(60ul, s.UsedPageCount);
            Assert.Equal("0xffff800000100000", Format.Hex(s.RamStart));
            Assert.Equal(10ul, s.TypeCounts[1]);
            Assert.Equal("60 (240.0 KiB)", s.Rows().Single(r => r[0] == "Used pages")[1]);
        }

        [Fact]
        public void WalkExtentsCoalescesExtensionsAndEmptyRuns()
        {
            byte[] rat = { 0, 0, 1, 128, 128, 3, 0, 128, 128, 5 };

            var extents = MemoryStats.WalkExtents(rat);

            Assert.Equal(new[] { (0, 2, 0), (2, 3, 1), (5, 1, 3), (6, 1, 0), (7, 2, 128), (9, 1, 5) },
                extents.Select(e => (e.Start, e.Length, (int)e.Type)));
        }

        [Theory]
        [InlineData(0ul, "0 B")]
        [InlineData(1023ul, "1023 B")]
        [InlineData(1536ul, "1.5 KiB")]
        [InlineData(5ul * 1024 * 1024 * 1024, "5.00 GiB")]
        public void FormatBytes(ulong n, string expected) => Assert.Equal(expected, Format.Bytes(n));
    }
}
