using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Cosmos.VisualStudio.Core
{
    /// <summary>
    /// Reads symbol addresses straight out of a 64-bit little-endian ELF's
    /// .symtab, so resolving the kernel's debug snapshot statics doesn't depend
    /// on <c>nm</c> being installed (it usually isn't on Windows).
    /// </summary>
    public static class ElfSymbols
    {
        private const int SHT_SYMTAB = 2;
        private const int SectionHeaderSize = 64;
        private const int SymbolSize = 24;

        /// <summary>Addresses of the <paramref name="names"/> found in the ELF; missing names are absent.</summary>
        public static Dictionary<string, ulong> Resolve(string elfPath, IEnumerable<string> names)
        {
            var found = new Dictionary<string, ulong>(StringComparer.Ordinal);
            var wanted = new HashSet<string>(names, StringComparer.Ordinal);
            if (wanted.Count == 0)
            {
                return found;
            }

            using (var fs = new FileStream(elfPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                byte[] header = ReadAt(fs, 0, 64);
                // \x7FELF, ELFCLASS64, ELFDATA2LSB
                if (header.Length < 64 || header[0] != 0x7f || header[1] != (byte)'E' || header[2] != (byte)'L' ||
                    header[3] != (byte)'F' || header[4] != 2 || header[5] != 1)
                {
                    return found;
                }

                long shoff = BitConverter.ToInt64(header, 0x28);
                int shentsize = BitConverter.ToUInt16(header, 0x3A);
                int shnum = BitConverter.ToUInt16(header, 0x3C);
                if (shoff <= 0 || shentsize < SectionHeaderSize || shnum == 0)
                {
                    return found;
                }

                byte[] sections = ReadAt(fs, shoff, shentsize * shnum);
                for (int s = 0; s < shnum; s++)
                {
                    int sh = s * shentsize;
                    if (sh + SectionHeaderSize > sections.Length || BitConverter.ToInt32(sections, sh + 4) != SHT_SYMTAB)
                    {
                        continue;
                    }
                    long symOffset = BitConverter.ToInt64(sections, sh + 0x18);
                    long symSize = BitConverter.ToInt64(sections, sh + 0x20);
                    int strIndex = BitConverter.ToInt32(sections, sh + 0x28);
                    long entSize = BitConverter.ToInt64(sections, sh + 0x38);
                    if (entSize <= 0)
                    {
                        entSize = SymbolSize;
                    }
                    if (strIndex <= 0 || strIndex >= shnum)
                    {
                        continue;
                    }

                    int strHeader = strIndex * shentsize;
                    long strOffset = BitConverter.ToInt64(sections, strHeader + 0x18);
                    long strSize = BitConverter.ToInt64(sections, strHeader + 0x20);

                    byte[] symtab = ReadAt(fs, symOffset, checked((int)symSize));
                    byte[] strtab = ReadAt(fs, strOffset, checked((int)strSize));

                    long count = symtab.Length / entSize;
                    for (long k = 0; k < count; k++)
                    {
                        int sym = (int)(k * entSize);
                        int nameOffset = BitConverter.ToInt32(symtab, sym);
                        if (nameOffset <= 0 || nameOffset >= strtab.Length)
                        {
                            continue;
                        }
                        string name = ReadCString(strtab, nameOffset);
                        if (wanted.Contains(name) && !found.ContainsKey(name))
                        {
                            found[name] = BitConverter.ToUInt64(symtab, sym + 8);
                            if (found.Count == wanted.Count)
                            {
                                return found;
                            }
                        }
                    }
                }
            }
            return found;
        }

        private static string ReadCString(byte[] table, int offset)
        {
            int end = Array.IndexOf(table, (byte)0, offset);
            if (end < 0)
            {
                end = table.Length;
            }
            return Encoding.ASCII.GetString(table, offset, end - offset);
        }

        private static byte[] ReadAt(Stream stream, long offset, int length)
        {
            if (offset < 0 || length <= 0 || offset >= stream.Length)
            {
                return Array.Empty<byte>();
            }
            length = (int)Math.Min(length, stream.Length - offset);
            var buffer = new byte[length];
            stream.Position = offset;
            int read = 0;
            while (read < length)
            {
                int n = stream.Read(buffer, read, length - read);
                if (n <= 0)
                {
                    break;
                }
                read += n;
            }
            return read == length ? buffer : buffer.Take(read).ToArray();
        }
    }
}
