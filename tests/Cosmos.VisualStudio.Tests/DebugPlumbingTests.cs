using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Cosmos.VisualStudio.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Cosmos.VisualStudio.Tests
{
    public class DebugPlumbingTests
    {
        /// <summary>A minimal ELF64 with a null section, a .symtab and its .strtab.</summary>
        private static byte[] BuildElf(params (string Name, ulong Value)[] symbols)
        {
            var strtab = new List<byte> { 0 };
            var symtab = new List<byte>(new byte[24]); // symbol 0 is null
            foreach (var (name, value) in symbols)
            {
                int nameOffset = strtab.Count;
                strtab.AddRange(Encoding.ASCII.GetBytes(name));
                strtab.Add(0);
                var sym = new byte[24];
                BitConverter.GetBytes(nameOffset).CopyTo(sym, 0);
                BitConverter.GetBytes(value).CopyTo(sym, 8);
                symtab.AddRange(sym);
            }

            const int headerSize = 64;
            int symOffset = headerSize;
            int strOffset = symOffset + symtab.Count;
            int shOffset = strOffset + strtab.Count;
            var elf = new byte[shOffset + 3 * 64];
            elf[0] = 0x7f; elf[1] = (byte)'E'; elf[2] = (byte)'L'; elf[3] = (byte)'F'; elf[4] = 2; elf[5] = 1;
            BitConverter.GetBytes((long)shOffset).CopyTo(elf, 0x28);
            BitConverter.GetBytes((ushort)64).CopyTo(elf, 0x3A);
            BitConverter.GetBytes((ushort)3).CopyTo(elf, 0x3C);
            symtab.CopyTo(elf, symOffset);
            strtab.CopyTo(elf, strOffset);

            int sym1 = shOffset + 64;
            BitConverter.GetBytes(2).CopyTo(elf, sym1 + 4);                     // SHT_SYMTAB
            BitConverter.GetBytes((long)symOffset).CopyTo(elf, sym1 + 0x18);
            BitConverter.GetBytes((long)symtab.Count).CopyTo(elf, sym1 + 0x20);
            BitConverter.GetBytes(2).CopyTo(elf, sym1 + 0x28);                  // link → .strtab
            BitConverter.GetBytes(24L).CopyTo(elf, sym1 + 0x38);
            int str2 = shOffset + 128;
            BitConverter.GetBytes(3).CopyTo(elf, str2 + 4);                     // SHT_STRTAB
            BitConverter.GetBytes((long)strOffset).CopyTo(elf, str2 + 0x18);
            BitConverter.GetBytes((long)strtab.Count).CopyTo(elf, str2 + 0x20);
            return elf;
        }

        [Fact]
        public void ElfSymbolsResolvesExactNames()
        {
            using var dir = new TempDir();
            string elf = dir.Combine("kernel.elf");
            File.WriteAllBytes(elf, BuildElf(
                ("__NONGCSTATICSFoo_Long", 0x1),
                (ThreadSnapshot.StaticsSymbol, 0xffff800000123450),
                (GCStats.StaticsSymbol, 0xffff800000200000)));

            var found = ElfSymbols.Resolve(elf, new[] { ThreadSnapshot.StaticsSymbol, GCStats.StaticsSymbol, MemoryStats.StaticsSymbol });

            Assert.Equal(0xffff800000123450ul, found[ThreadSnapshot.StaticsSymbol]);
            Assert.Equal(0xffff800000200000ul, found[GCStats.StaticsSymbol]);
            Assert.False(found.ContainsKey(MemoryStats.StaticsSymbol));
        }

        [Fact]
        public void ElfSymbolsIgnoresNonElfFiles()
        {
            using var dir = new TempDir();
            string file = dir.Write("kernel.iso", "not an elf at all, just some text padding it out past 64 bytes........");
            Assert.Empty(ElfSymbols.Resolve(file, new[] { "x" }));
        }

        [Fact]
        public void ParseMonitorHexDump()
        {
            string text = "ffff800000040020: 0x01 0x00 0x5d 0xc0\r\nffff800000040024: 0xff 0x10\r\n";
            Assert.Equal(new byte[] { 0x01, 0x00, 0x5d, 0xc0, 0xff }, QmpClient.ParseMonitorHexDump(text, 5));
            Assert.Equal(new byte[] { 0x01, 0x00, 0x5d, 0xc0, 0xff, 0x10 }, QmpClient.ParseMonitorHexDump(text, 8));
        }

        [Fact]
        public async Task QmpClientHandshakesAndReadsMemory()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var commands = new List<string>();

            Task server = Task.Run(async () =>
            {
                using TcpClient peer = await listener.AcceptTcpClientAsync();
                using var reader = new StreamReader(peer.GetStream());
                using var writer = new StreamWriter(peer.GetStream()) { AutoFlush = true, NewLine = "\n" };
                await writer.WriteLineAsync("{\"QMP\": {\"version\": {}, \"capabilities\": []}}");
                for (int i = 0; i < 2; i++)
                {
                    string line = await reader.ReadLineAsync();
                    commands.Add(line!);
                    var cmd = JObject.Parse(line!);
                    if ((string)cmd["execute"]! == "qmp_capabilities")
                    {
                        await writer.WriteLineAsync("{\"return\": {}}");
                    }
                    else
                    {
                        // An event arriving before the reply must be skipped.
                        await writer.WriteLineAsync("{\"event\": \"RTC_CHANGE\", \"data\": {}}");
                        await writer.WriteLineAsync("{\"return\": \"ffff800000001000: 0x2a 0x00 0x00 0x00\\r\\n\"}");
                    }
                }
            });

            using (var qmp = new QmpClient("127.0.0.1", port))
            {
                await qmp.ConnectAsync(TimeSpan.FromSeconds(5));
                byte[] bytes = await qmp.ReadVirtualAsync(0xffff800000001000, 4);
                Assert.Equal(new byte[] { 0x2a, 0, 0, 0 }, bytes);
            }
            await server;
            listener.Stop();

            Assert.Contains("x /4bx 0xffff800000001000", commands[1]);
        }

        [Fact]
        public async Task PortCheckSeesListeners()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Assert.True(await PortCheck.IsPortInUseAsync(port));
            listener.Stop();
            Assert.False(await PortCheck.IsPortInUseAsync(port));
        }

        [Fact]
        public void ParseToolsCheck()
        {
            string json = "Checking...\n{\n  \"platform\": \"windows\",\n  \"tools\": [\n" +
                          "    { \"name\": \"gdb-multiarch\", \"displayName\": \"GDB\", \"found\": true, \"required\": true, \"version\": \"15.1\", \"path\": \"C:\\\\gdb\\\\bin\\\\gdb-multiarch.exe\" },\n" +
                          "    { \"name\": \"qemu\", \"displayName\": \"QEMU\", \"found\": false, \"required\": true, \"version\": null, \"path\": null }\n  ]\n}\n";

            var tools = CosmosTools.ParseToolsCheck(json)!;

            Assert.Equal(2, tools.Count);
            Assert.Equal("C:\\gdb\\bin\\gdb-multiarch.exe", tools[0].Path);
            Assert.False(tools[1].Found);
            Assert.Null(tools[1].Version);
            Assert.Null(CosmosTools.ParseToolsCheck("garbage"));
        }

        [Fact]
        public void ParseToolVersion()
        {
            string list = "Package Id      Version      Commands\n-------------------------------------\ncosmos.tools    3.0.37       cosmos\n";
            Assert.Equal("3.0.37", CosmosTools.ParseToolVersion(list, "cosmos.tools"));
            Assert.Null(CosmosTools.ParseToolVersion(list, "other"));
        }
    }
}
