using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace AstueMpsReplacement
{
    // Small ZIP reader/writer for .NET Framework 4.0.
    // Supports the only formats used by ASTUE/MPS here: stored (0) and deflate (8), no encryption, no ZIP64.
    internal static class SimpleZip
    {
        internal sealed class Entry
        {
            public string Name;
            public byte[] Data;

            public Entry(string name, byte[] data)
            {
                Name = name ?? string.Empty;
                Data = data ?? new byte[0];
            }
        }

        private sealed class EntryInfo
        {
            public string Name;
            public ushort Flags;
            public ushort Method;
            public uint Crc32;
            public uint CompressedSize;
            public uint UncompressedSize;
            public uint LocalHeaderOffset;
        }

        private sealed class WrittenEntry
        {
            public string Name;
            public byte[] NameBytes;
            public byte[] Data;
            public byte[] Compressed;
            public ushort Method;
            public ushort DosTime;
            public ushort DosDate;
            public uint Crc32;
            public uint LocalOffset;
        }

        public static bool HasEntry(string zipPath, string entryName)
        {
            return FindEntry(zipPath, entryName) != null;
        }

        public static byte[] ReadEntry(string zipPath, string entryName)
        {
            if (zipPath == null) throw new ArgumentNullException("zipPath");
            if (entryName == null) throw new ArgumentNullException("entryName");

            EntryInfo info;
            using (var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                info = FindEntry(fs, entryName);
                if (info == null) return null;
                return ReadEntryData(fs, info);
            }
        }

        public static void WriteArchive(string zipPath, IEnumerable<Entry> entries)
        {
            if (zipPath == null) throw new ArgumentNullException("zipPath");
            if (entries == null) throw new ArgumentNullException("entries");

            var prepared = new List<WrittenEntry>();
            foreach (var source in entries)
            {
                if (source == null) continue;
                var name = (source.Name ?? string.Empty).Replace('\\', '/');
                if (name.Length == 0) throw new InvalidDataException("ZIP entry name is empty.");
                var data = source.Data ?? new byte[0];
                var compressed = Deflate(data);
                ushort method = 8;
                if (compressed.Length >= data.Length)
                {
                    compressed = data;
                    method = 0;
                }

                ushort dosTime, dosDate;
                ToDosDateTime(DateTime.Now, out dosTime, out dosDate);
                prepared.Add(new WrittenEntry
                {
                    Name = name,
                    NameBytes = Encoding.UTF8.GetBytes(name),
                    Data = data,
                    Compressed = compressed,
                    Method = method,
                    DosTime = dosTime,
                    DosDate = dosDate,
                    Crc32 = Crc32(data)
                });
            }

            using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var bw = new BinaryWriter(fs, Encoding.UTF8))
            {
                foreach (var e in prepared)
                {
                    if (fs.Position > uint.MaxValue) throw new InvalidDataException("ZIP64 is not supported.");
                    e.LocalOffset = (uint)fs.Position;
                    WriteUInt32(bw, 0x04034b50);
                    WriteUInt16(bw, 20);             // version needed
                    WriteUInt16(bw, 0x0800);         // UTF-8 names
                    WriteUInt16(bw, e.Method);
                    WriteUInt16(bw, e.DosTime);
                    WriteUInt16(bw, e.DosDate);
                    WriteUInt32(bw, e.Crc32);
                    WriteUInt32(bw, CheckedUInt(e.Compressed.Length));
                    WriteUInt32(bw, CheckedUInt(e.Data.Length));
                    WriteUInt16(bw, CheckedUShort(e.NameBytes.Length));
                    WriteUInt16(bw, 0);
                    bw.Write(e.NameBytes);
                    bw.Write(e.Compressed);
                }

                if (fs.Position > uint.MaxValue) throw new InvalidDataException("ZIP64 is not supported.");
                var centralOffset = (uint)fs.Position;

                foreach (var e in prepared)
                {
                    WriteUInt32(bw, 0x02014b50);
                    WriteUInt16(bw, 20);             // version made by
                    WriteUInt16(bw, 20);             // version needed
                    WriteUInt16(bw, 0x0800);
                    WriteUInt16(bw, e.Method);
                    WriteUInt16(bw, e.DosTime);
                    WriteUInt16(bw, e.DosDate);
                    WriteUInt32(bw, e.Crc32);
                    WriteUInt32(bw, CheckedUInt(e.Compressed.Length));
                    WriteUInt32(bw, CheckedUInt(e.Data.Length));
                    WriteUInt16(bw, CheckedUShort(e.NameBytes.Length));
                    WriteUInt16(bw, 0);              // extra
                    WriteUInt16(bw, 0);              // comment
                    WriteUInt16(bw, 0);              // disk
                    WriteUInt16(bw, 0);              // internal attrs
                    WriteUInt32(bw, 0);              // external attrs
                    WriteUInt32(bw, e.LocalOffset);
                    bw.Write(e.NameBytes);
                }

                if (fs.Position > uint.MaxValue) throw new InvalidDataException("ZIP64 is not supported.");
                var centralSize = (uint)fs.Position - centralOffset;
                if (prepared.Count > ushort.MaxValue) throw new InvalidDataException("Too many ZIP entries.");

                WriteUInt32(bw, 0x06054b50);
                WriteUInt16(bw, 0);
                WriteUInt16(bw, 0);
                WriteUInt16(bw, (ushort)prepared.Count);
                WriteUInt16(bw, (ushort)prepared.Count);
                WriteUInt32(bw, centralSize);
                WriteUInt32(bw, centralOffset);
                WriteUInt16(bw, 0);
                bw.Flush();
            }
        }

        private static EntryInfo FindEntry(string zipPath, string entryName)
        {
            using (var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                return FindEntry(fs, entryName);
        }

        private static EntryInfo FindEntry(FileStream fs, string entryName)
        {
            long centralOffset;
            int entryCount;
            LocateCentralDirectory(fs, out centralOffset, out entryCount);
            fs.Position = centralOffset;
            var br = new BinaryReader(fs, Encoding.UTF8);
            for (var i = 0; i < entryCount; i++)
                {
                    if (ReadUInt32(br) != 0x02014b50)
                        throw new InvalidDataException("Некорректный ZIP central directory.");
                    ReadUInt16(br); // made by
                    ReadUInt16(br); // needed
                    var flags = ReadUInt16(br);
                    var method = ReadUInt16(br);
                    ReadUInt16(br); // time
                    ReadUInt16(br); // date
                    var crc = ReadUInt32(br);
                    var csize = ReadUInt32(br);
                    var usize = ReadUInt32(br);
                    var nameLen = ReadUInt16(br);
                    var extraLen = ReadUInt16(br);
                    var commentLen = ReadUInt16(br);
                    ReadUInt16(br); // disk
                    ReadUInt16(br); // int attrs
                    ReadUInt32(br); // ext attrs
                    var localOffset = ReadUInt32(br);
                    var nameBytes = br.ReadBytes(nameLen);
                    if (nameBytes.Length != nameLen) throw new EndOfStreamException();
                    var name = DecodeName(nameBytes, flags);
                    if (extraLen > 0) br.ReadBytes(extraLen);
                    if (commentLen > 0) br.ReadBytes(commentLen);

                    if (string.Equals(name, entryName, StringComparison.OrdinalIgnoreCase))
                    {
                        if ((flags & 0x0001) != 0)
                            throw new NotSupportedException("Зашифрованные ZIP entries не поддерживаются.");
                        if (method != 0 && method != 8)
                            throw new NotSupportedException("ZIP compression method " + method + " не поддерживается.");
                        return new EntryInfo
                        {
                            Name = name,
                            Flags = flags,
                            Method = method,
                            Crc32 = crc,
                            CompressedSize = csize,
                            UncompressedSize = usize,
                            LocalHeaderOffset = localOffset
                        };
                    }
                }
            return null;
        }

        private static byte[] ReadEntryData(FileStream fs, EntryInfo info)
        {
            fs.Position = info.LocalHeaderOffset;
            using (var br = new BinaryReader(fs, Encoding.UTF8))
            {
                if (ReadUInt32(br) != 0x04034b50)
                    throw new InvalidDataException("Некорректный ZIP local header.");
                ReadUInt16(br); // needed
                var flags = ReadUInt16(br);
                var method = ReadUInt16(br);
                ReadUInt16(br); ReadUInt16(br); // time/date
                ReadUInt32(br); ReadUInt32(br); ReadUInt32(br); // crc/sizes may be zero with descriptor
                var nameLen = ReadUInt16(br);
                var extraLen = ReadUInt16(br);
                if ((flags & 0x0001) != 0) throw new NotSupportedException("Зашифрованные ZIP entries не поддерживаются.");
                if (method != info.Method) throw new InvalidDataException("ZIP method mismatch.");
                fs.Position += nameLen + extraLen;
                if (info.CompressedSize > int.MaxValue || info.UncompressedSize > int.MaxValue)
                    throw new NotSupportedException("ZIP64/entries >2GB не поддерживаются.");
                var compressed = br.ReadBytes((int)info.CompressedSize);
                if (compressed.Length != (int)info.CompressedSize) throw new EndOfStreamException();
                byte[] data;
                if (info.Method == 0)
                {
                    data = compressed;
                }
                else
                {
                    using (var input = new MemoryStream(compressed, false))
                    using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
                    using (var output = new MemoryStream(info.UncompressedSize > 0 ? (int)info.UncompressedSize : 0))
                    {
                        CopyStream(deflate, output);
                        data = output.ToArray();
                    }
                }
                if (info.UncompressedSize != 0 && data.Length != (int)info.UncompressedSize)
                    throw new InvalidDataException("ZIP entry size mismatch: " + info.Name);
                if (Crc32(data) != info.Crc32)
                    throw new InvalidDataException("ZIP CRC mismatch: " + info.Name);
                return data;
            }
        }

        private static void LocateCentralDirectory(FileStream fs, out long centralOffset, out int entryCount)
        {
            if (fs.Length < 22) throw new InvalidDataException("Файл слишком мал для ZIP.");
            var scan = (int)Math.Min(fs.Length, 22 + 65535);
            var buffer = new byte[scan];
            fs.Position = fs.Length - scan;
            var got = fs.Read(buffer, 0, buffer.Length);
            for (var i = got - 22; i >= 0; i--)
            {
                if (buffer[i] == 0x50 && buffer[i + 1] == 0x4b && buffer[i + 2] == 0x05 && buffer[i + 3] == 0x06)
                {
                    var disk = U16(buffer, i + 4);
                    var centralDisk = U16(buffer, i + 6);
                    var entriesDisk = U16(buffer, i + 8);
                    var entries = U16(buffer, i + 10);
                    var size = U32(buffer, i + 12);
                    var offset = U32(buffer, i + 16);
                    if (disk != 0 || centralDisk != 0 || entriesDisk != entries)
                        throw new NotSupportedException("Multi-disk ZIP не поддерживается.");
                    if (entries == 0xffff || size == 0xffffffff || offset == 0xffffffff)
                        throw new NotSupportedException("ZIP64 не поддерживается.");
                    centralOffset = offset;
                    entryCount = entries;
                    return;
                }
            }
            throw new InvalidDataException("ZIP EOCD не найден.");
        }

        private static byte[] Deflate(byte[] data)
        {
            using (var output = new MemoryStream())
            {
                using (var deflate = new DeflateStream(output, CompressionMode.Compress))
                    deflate.Write(data, 0, data.Length);
                return output.ToArray();
            }
        }

        private static void CopyStream(Stream input, Stream output)
        {
            var buffer = new byte[64 * 1024];
            int n;
            while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                output.Write(buffer, 0, n);
        }

        private static string DecodeName(byte[] bytes, ushort flags)
        {
            if ((flags & 0x0800) != 0) return Encoding.UTF8.GetString(bytes);
            try { return Encoding.GetEncoding(437).GetString(bytes); }
            catch { return Encoding.Default.GetString(bytes); }
        }

        private static void ToDosDateTime(DateTime dt, out ushort time, out ushort date)
        {
            if (dt.Year < 1980) dt = new DateTime(1980, 1, 1);
            if (dt.Year > 2107) dt = new DateTime(2107, 12, 31, 23, 59, 58);
            time = (ushort)((dt.Hour << 11) | (dt.Minute << 5) | (dt.Second / 2));
            date = (ushort)(((dt.Year - 1980) << 9) | (dt.Month << 5) | dt.Day);
        }

        private static uint Crc32(byte[] data)
        {
            uint crc = 0xffffffff;
            for (var i = 0; i < data.Length; i++)
            {
                crc ^= data[i];
                for (var b = 0; b < 8; b++)
                    crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0u);
            }
            return ~crc;
        }

        private static ushort U16(byte[] b, int o) { return (ushort)(b[o] | (b[o + 1] << 8)); }
        private static uint U32(byte[] b, int o) { return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24)); }
        private static ushort ReadUInt16(BinaryReader br) { return br.ReadUInt16(); }
        private static uint ReadUInt32(BinaryReader br) { return br.ReadUInt32(); }
        private static void WriteUInt16(BinaryWriter bw, ushort v) { bw.Write(v); }
        private static void WriteUInt32(BinaryWriter bw, uint v) { bw.Write(v); }
        private static uint CheckedUInt(int value) { if (value < 0) throw new ArgumentOutOfRangeException("value"); return (uint)value; }
        private static ushort CheckedUShort(int value) { if (value < 0 || value > ushort.MaxValue) throw new InvalidDataException("ZIP field too long."); return (ushort)value; }
    }
}
