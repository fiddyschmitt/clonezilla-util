using libCommon;
using System.Buffers.Binary;
using System.Text;

namespace clonezilla_util_tests.Synthetic
{
    /// <summary>
    /// Writes a GPT disk image around given partition contents: protective MBR, primary header and entry array, the
    /// partitions (1 MiB aligned, typed "Microsoft basic data"), then the backup entry array and header. That is all
    /// 7-Zip's GPT handler checks: the MBR boot signature, "EFI PART", both CRCs, and that the header sits at LBA 1.
    /// The file is sparse and only the partitions' non-zero chunks are written, so a 100 MB disk costs a few MB.
    /// </summary>
    static class GptDiskImage
    {
        const int Sector = 512;
        const int EntryCount = 128;
        const int EntrySize = 128;
        const int EntryArraySectors = EntryCount * EntrySize / Sector;
        const long Alignment = 2048;   //sectors: 1 MiB

        static readonly Guid BasicDataPartition = new("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7");

        public static void Write(string path, IReadOnlyList<(string Label, Stream Content)> partitions)
        {
            var layout = new List<(long FirstLba, long LastLba)>();
            var nextLba = Alignment;
            foreach (var (_, content) in partitions)
            {
                var sectors = (content.Length + Sector - 1) / Sector;
                layout.Add((nextLba, nextLba + sectors - 1));
                nextLba = (nextLba + sectors + Alignment - 1) / Alignment * Alignment;
            }
            var backupEntriesLba = nextLba;
            var backupHeaderLba = backupEntriesLba + EntryArraySectors;
            var totalSectors = backupHeaderLba + 1;

            var entries = new byte[EntryCount * EntrySize];
            for (var i = 0; i < partitions.Count; i++)
            {
                var entry = entries.AsSpan(i * EntrySize, EntrySize);
                BasicDataPartition.ToByteArray().CopyTo(entry);                 //GPT stores GUIDs in this mixed-endian form
                Guid.NewGuid().ToByteArray().CopyTo(entry[16..]);
                BinaryPrimitives.WriteInt64LittleEndian(entry[32..], layout[i].FirstLba);
                BinaryPrimitives.WriteInt64LittleEndian(entry[40..], layout[i].LastLba);   //inclusive
                Encoding.Unicode.GetBytes(partitions[i].Label).CopyTo(entry[56..]);       //UTF-16LE, up to 36 characters
            }
            var entriesCrc = Crc32(entries);
            var diskGuid = Guid.NewGuid();

            using var disk = File.Create(path);
            disk.SafeFileHandle.MarkAsSparse();
            disk.SetLength(totalSectors * Sector);

            //protective MBR: one partition of type 0xEE covering the disk
            var mbr = new byte[Sector];
            var record = mbr.AsSpan(0x1BE, 16);
            record[1] = 0x00; record[2] = 0x02; record[3] = 0x00;           //CHS of LBA 1
            record[4] = 0xEE;
            record[5] = 0xFF; record[6] = 0xFF; record[7] = 0xFF;
            BinaryPrimitives.WriteUInt32LittleEndian(record[8..], 1);
            BinaryPrimitives.WriteUInt32LittleEndian(record[12..], (uint)Math.Min(totalSectors - 1, uint.MaxValue));
            mbr[0x1FE] = 0x55;
            mbr[0x1FF] = 0xAA;
            WriteAt(disk, 0, mbr);

            var firstUsable = 2L + EntryArraySectors;
            var lastUsable = backupEntriesLba - 1;
            WriteAt(disk, 1 * Sector, Header(1, backupHeaderLba, 2, firstUsable, lastUsable, diskGuid, entriesCrc));
            WriteAt(disk, 2 * Sector, entries);

            var chunk = new byte[1024 * 1024];
            for (var i = 0; i < partitions.Count; i++)
            {
                var content = partitions[i].Content;
                content.Seek(0, SeekOrigin.Begin);
                var offset = layout[i].FirstLba * Sector;
                int read;
                while ((read = content.ReadAtLeast(chunk, chunk.Length, throwOnEndOfStream: false)) > 0)
                {
                    if (chunk.AsSpan(0, read).ContainsAnyExcept((byte)0))
                    {
                        WriteAt(disk, offset, chunk.AsSpan(0, read));
                    }
                    offset += read;
                }
            }

            WriteAt(disk, backupEntriesLba * Sector, entries);
            WriteAt(disk, backupHeaderLba * Sector, Header(backupHeaderLba, 1, backupEntriesLba, firstUsable, lastUsable, diskGuid, entriesCrc));
        }

        static byte[] Header(long myLba, long alternateLba, long entriesLba, long firstUsableLba, long lastUsableLba, Guid diskGuid, uint entriesCrc)
        {
            var header = new byte[Sector];
            var h = header.AsSpan();
            "EFI PART"u8.CopyTo(h);
            BinaryPrimitives.WriteUInt32LittleEndian(h[8..], 0x00010000);   //revision 1.0
            BinaryPrimitives.WriteUInt32LittleEndian(h[12..], 92);          //header size
            //16: header CRC32, computed below over the 92 bytes with this field zero; 20: reserved
            BinaryPrimitives.WriteInt64LittleEndian(h[24..], myLba);
            BinaryPrimitives.WriteInt64LittleEndian(h[32..], alternateLba);
            BinaryPrimitives.WriteInt64LittleEndian(h[40..], firstUsableLba);
            BinaryPrimitives.WriteInt64LittleEndian(h[48..], lastUsableLba);
            diskGuid.ToByteArray().CopyTo(h[56..]);
            BinaryPrimitives.WriteInt64LittleEndian(h[72..], entriesLba);
            BinaryPrimitives.WriteUInt32LittleEndian(h[80..], EntryCount);
            BinaryPrimitives.WriteUInt32LittleEndian(h[84..], EntrySize);
            BinaryPrimitives.WriteUInt32LittleEndian(h[88..], entriesCrc);
            BinaryPrimitives.WriteUInt32LittleEndian(h[16..], Crc32(h[..92]));
            return header;
        }

        static void WriteAt(FileStream file, long offset, ReadOnlySpan<byte> bytes)
        {
            file.Seek(offset, SeekOrigin.Begin);
            file.Write(bytes);
        }

        //standard CRC-32 (reflected 0xEDB88320, initial and final XOR 0xFFFFFFFF), as GPT uses
        static uint Crc32(ReadOnlySpan<byte> data)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var b in data)
            {
                crc ^= b;
                for (var k = 0; k < 8; k++)
                {
                    crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
                }
            }
            return ~crc;
        }
    }
}
