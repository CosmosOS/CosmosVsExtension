using System;
using System.IO;
using System.Linq;
using Cosmos.VisualStudio.Core;
using Xunit;

namespace Cosmos.VisualStudio.Tests
{
    public class QemuOptionsTests
    {
        [Theory]
        [InlineData("512M", 512)]
        [InlineData("512", 512)]
        [InlineData("1G", 1024)]
        [InlineData("2g", 2048)]
        public void ParseMemoryMb(string text, int expected) => Assert.Equal(expected, QemuOptions.ParseMemoryMb(text));

        [Fact]
        public void ParseMemoryMbRejectsGarbage() => Assert.Null(QemuOptions.ParseMemoryMb("lots"));

        [Theory]
        [InlineData("256M", 256L * 1024 * 1024)]
        [InlineData("256", 256L * 1024 * 1024)]
        [InlineData("1G", 1024L * 1024 * 1024)]
        [InlineData("512K", 512L * 1024)]
        public void ParseSizeBytes(string text, long expected) => Assert.Equal(expected, QemuOptions.ParseSizeBytes(text));

        [Fact]
        public void SplitExtraArgsKeepsQuotedGroups()
        {
            Assert.Equal(new[] { "-name", "\"my vm\"", "-drive", "file=\"a b.img\",if=none" },
                QemuOptions.SplitExtraArgs("-name \"my vm\"  -drive file=\"a b.img\",if=none"));
            Assert.Empty(QemuOptions.SplitExtraArgs("   "));
        }

        [Fact]
        public void DeviceArgs()
        {
            Assert.Empty(QemuOptions.BuildAudioArgs("none"));
            Assert.Equal(new[] { "--audio", "intel-hda" }, QemuOptions.BuildAudioArgs("intel-hda"));
            Assert.Equal(new[] { "--nic", "none" }, QemuOptions.BuildNicArgs("none"));
            Assert.Empty(QemuOptions.BuildCpuArgs(" "));
            Assert.Equal(new[] { "--hostfwd", "tcp::2323-:23" }, QemuOptions.BuildHostForwardArgs(new[] { " tcp::2323-:23 ", "" }));
            Assert.Equal(new[] { "--keyboard", "ps2", "--mouse", "none" }, QemuOptions.BuildInputArgs("ps2", "none"));
        }

        [Fact]
        public void PrepareDiskArgsCreatesMissingImagesOnly()
        {
            using var dir = new TempDir();
            string existing = dir.Write("disks/old.img", "data");
            var disks = new[]
            {
                new DiskConfig { Path = "disks/new.img", Type = "nvme", Size = "1M" },
                new DiskConfig { Path = existing, Type = "ahci", Size = "1G" },
                new DiskConfig { Path = "  " }
            };
            var notes = new System.Collections.Generic.List<string>();

            var args = QemuOptions.PrepareDiskArgs(dir.Path, disks, notes.Add);

            string created = dir.Combine("disks/new.img");
            Assert.Equal(new[] { "--disk", created + ",nvme", "--disk", existing + ",ahci" }, args);
            Assert.Equal(1024 * 1024, new FileInfo(created).Length);
            Assert.Equal(4, new FileInfo(existing).Length);
            Assert.Single(notes);
        }

        [Fact]
        public void PrepareDiskArgsRejectsBadSizes()
        {
            using var dir = new TempDir();
            Assert.Throws<CosmosException>(() =>
                QemuOptions.PrepareDiskArgs(dir.Path, new[] { new DiskConfig { Path = "x.img", Size = "big" } }));
        }

        [Theory]
        [InlineData("plain", "plain")]
        [InlineData("", "\"\"")]
        [InlineData("a b", "\"a b\"")]
        [InlineData("\"my vm\"", "\"\\\"my vm\\\"\"")]
        [InlineData("C:\\dir with space\\", "\"C:\\dir with space\\\\\"")]
        [InlineData("a\\\\b", "a\\\\b")]
        public void CommandLineQuote(string arg, string expected) => Assert.Equal(expected, CommandLine.Quote(arg));
    }
}
