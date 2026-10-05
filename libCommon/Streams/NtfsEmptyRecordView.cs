using Serilog;
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;

namespace libCommon.Streams
{
    /// <summary>
    /// A read-only view of a partition image for its readers (the native 7-Zip workers, and the .img that
    /// mount-as-image-files serves) which presents an NTFS volume 7-Zip will open.
    ///
    /// 7-Zip's NTFS reader refuses the whole volume when any MFT record has a signature it does not know. An unused
    /// record that NTFS has zeroed is fine ("empty") - unless the driver has since stamped it: NTFS writes a 16-bit
    /// update sequence number (USN) into the last two bytes of each sector of a record, keeping the originals in an
    /// array at the offset named in the record header. In a zeroed record that offset is 0, so the USN lands in the
    /// record's first two bytes - where 7-Zip reads the signature. It accepts such a "signature" only up to 5, and
    /// the USN counts up every time the record is rewritten, so a volume that Windows uses happily can be a volume
    /// 7-Zip calls "not an archive" (gh #79: one record with USN 7 hid 34,146 files).
    ///
    /// This view zeroes those two bytes as the record is read, for records inside the MFT whose first four bytes are
    /// a 16-bit value above 7-Zip's limit followed by two zero bytes. No live record can start that way: FILE, BAAD,
    /// INDX and the other signatures all have non-zero third and fourth bytes. Records 7-Zip accepts as they are
    /// (USN 0 to 5) are left alone, as is everything else, and
    /// the partition image itself is never modified (extract-partition-image reads the original stream, not this
    /// view). The MFT's location is found once, lazily, from the boot sector and MFT record 0; if the image is not
    /// NTFS, or its MFT cannot be located, the view is a plain pass-through.
    ///
    /// Reads are positional and stateless, so any number of cursors can read through one view concurrently, as they
    /// do through the source. Disposing the view leaves the source open: it belongs to the partition.
    /// </summary>
    public sealed class NtfsEmptyRecordView : Stream, IPositionalReader
    {
        readonly Stream inner;
        readonly IPositionalReader? positional;
        /// <summary>The largest "signature" 7-Zip's NTFS reader accepts as an empty record (NtfsHandler.cpp,
        /// CMftRec::Is_Magic_Empty: k_Magic_Unused_MAX = 5, unchanged from 22.02 through 26.02). Only records above it
        /// are presented as empty; the rest 7-Zip handles itself.</summary>
        public const ushort SevenZipAcceptsUpTo = 5;

        readonly object seekLock = new();   //serialises seek+read on a source that has no positional reads
        readonly string name;
        readonly Lazy<MftLayout?> layout;
        readonly ConcurrentDictionary<long, byte> reported = new();
        long position;

        /// <summary>Wraps <paramref name="inner"/>; <paramref name="name"/> prefixes the log lines (e.g. "[container] [sda1]").</summary>
        public NtfsEmptyRecordView(Stream inner, string name)
        {
            this.inner = inner;
            positional = inner as IPositionalReader;
            this.name = name;
            layout = new Lazy<MftLayout?>(Discover, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);
        }

        /// <summary>True once the MFT has been located (forces discovery). Exposed for tests.</summary>
        public bool IsNtfs => layout.Value != null;

        /// <summary>The MFT record numbers this view has presented as empty so far. Exposed for tests.</summary>
        public IReadOnlyCollection<long> PatchedRecords => (IReadOnlyCollection<long>)reported.Keys;

        public int ReadAt(long readPosition, byte[] buffer, int offset, int count)
        {
            var n = RawReadAt(readPosition, buffer, offset, count);
            if (n > 0) Patch(readPosition, buffer, offset, n);
            return n;
        }

        int RawReadAt(long readPosition, byte[] buffer, int offset, int count)
        {
            if (positional != null) return positional.ReadAt(readPosition, buffer, offset, count);

            lock (seekLock)
            {
                inner.Seek(readPosition, SeekOrigin.Begin);
                return inner.Read(buffer, offset, count);
            }
        }

        //fills the buffer from the source, or returns false at end of stream
        bool ReadExactly(long readPosition, byte[] buffer, int count)
        {
            var total = 0;
            while (total < count)
            {
                var n = RawReadAt(readPosition + total, buffer, total, count - total);
                if (n <= 0) return false;
                total += n;
            }
            return true;
        }

        void Patch(long readPosition, byte[] buffer, int offset, int n)
        {
            var mft = layout.Value;
            if (mft == null) return;

            var readEnd = readPosition + n;
            foreach (var run in mft.Runs)
            {
                var runEnd = run.DiskStart + run.Length;
                var start = Math.Max(readPosition, run.DiskStart);
                var end = Math.Min(readEnd, runEnd);
                if (start >= end) continue;

                //the records (by MFT-relative offset) that this part of the read touches
                var logicalStart = run.LogicalStart + (start - run.DiskStart);
                var logicalEnd = run.LogicalStart + (end - run.DiskStart);
                var firstRecord = logicalStart / mft.RecordSize;
                var lastRecord = (logicalEnd - 1) / mft.RecordSize;

                for (var record = firstRecord; record <= lastRecord && record < mft.RecordCount; record++)
                {
                    //only the record's first two bytes are ever changed; a read that holds neither has nothing to do
                    var recordLogical = record * mft.RecordSize;
                    if (recordLogical + 2 <= logicalStart || recordLogical >= logicalEnd) continue;

                    //Records are aligned to their size (or the cluster size, whichever is smaller), and runs are
                    //cluster-aligned, so a record's first four bytes lie in one cluster - the one holding its first
                    //byte, which is in this run.
                    var headDisk = run.DiskStart + (recordLogical - run.LogicalStart);
                    if (!IsStampedEmptyRecord(headDisk, readPosition, buffer, offset, n, out var usn)) continue;

                    for (var b = headDisk; b < headDisk + 2; b++)
                    {
                        if (b >= readPosition && b < readEnd)
                        {
                            buffer[offset + (int)(b - readPosition)] = 0;
                        }
                    }
                    Report(record, usn);
                }
            }
        }

        //The record's first four bytes, taken from the caller's buffer when the read holds all four, otherwise
        //fetched from the source: a 16-bit value 7-Zip rejects, followed by two zero bytes, is a stamped empty
        //record that needs presenting as empty.
        bool IsStampedEmptyRecord(long headDisk, long readPosition, byte[] buffer, int offset, int n, out ushort usn)
        {
            byte b0, b1, b2, b3;
            if (headDisk >= readPosition && headDisk + 4 <= readPosition + n)
            {
                var i = offset + (int)(headDisk - readPosition);
                (b0, b1, b2, b3) = (buffer[i], buffer[i + 1], buffer[i + 2], buffer[i + 3]);
            }
            else
            {
                var head = new byte[4];
                if (!ReadExactly(headDisk, head, 4))
                {
                    usn = 0;
                    return false;
                }
                (b0, b1, b2, b3) = (head[0], head[1], head[2], head[3]);
            }

            usn = (ushort)(b0 | (b1 << 8));
            return usn > SevenZipAcceptsUpTo && b2 == 0 && b3 == 0;
        }

        void Report(long record, ushort usn)
        {
            if (!reported.TryAdd(record, 0)) return;

            var message = $"{name} NTFS MFT record {record:N0} is an unused, zeroed record stamped with update sequence number {usn} where 7-Zip expects the record signature; presenting it as empty so 7-Zip opens the volume (gh #79).";
            if (reported.Count <= 5) Log.Information(message);
            else Log.Debug(message);
        }

        //----- locating the MFT (once) -----

        sealed record MftRun(long LogicalStart, long DiskStart, long Length);
        sealed record MftLayout(int RecordSize, long RecordCount, IReadOnlyList<MftRun> Runs);

        MftLayout? Discover()
        {
            try
            {
                return DiscoverCore();
            }
            catch (Exception ex)
            {
                Log.Debug($"{name} Could not locate the NTFS MFT ({ex.Message}); serving the image unchanged.");
                return null;
            }
        }

        MftLayout? DiscoverCore()
        {
            var boot = new byte[512];
            if (!ReadExactly(0, boot, boot.Length)) return null;
            if (!boot.AsSpan(3, 8).SequenceEqual("NTFS    "u8)) return null;   //not NTFS: nothing to do

            var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(0x0B));
            var sectorsPerCluster = boot[0x0D];
            var mftCluster = BinaryPrimitives.ReadInt64LittleEndian(boot.AsSpan(0x30));
            var clustersPerRecord = (sbyte)boot[0x40];

            if (bytesPerSector is not (512 or 1024 or 2048 or 4096) || sectorsPerCluster == 0)
            {
                Log.Debug($"{name} NTFS boot sector has an unexpected geometry (sector {bytesPerSector}, sectors per cluster {sectorsPerCluster}); serving the image unchanged.");
                return null;
            }
            //values above 0x80 encode a power of two (large clusters, NTFS 3.1+)
            var clusterSize = sectorsPerCluster < 0x80 ? (long)bytesPerSector * sectorsPerCluster : (long)bytesPerSector << (256 - sectorsPerCluster);
            var recordSize = clustersPerRecord < 0 ? 1L << -clustersPerRecord : clustersPerRecord * clusterSize;
            if (clusterSize > 2 * 1024 * 1024 || recordSize < 512 || recordSize > 64 * 1024 || (recordSize & (recordSize - 1)) != 0)
            {
                Log.Debug($"{name} NTFS boot sector has an unexpected cluster size ({clusterSize}) or MFT record size ({recordSize}); serving the image unchanged.");
                return null;
            }

            //MFT record 0 describes the MFT itself: its $DATA runs are where every record lives
            var record0 = new byte[recordSize];
            if (mftCluster <= 0 || !ReadExactly(mftCluster * clusterSize, record0, record0.Length)) return null;
            if (!record0.AsSpan(0, 4).SequenceEqual("FILE"u8)) return null;
            if (!ApplyFixup(record0, bytesPerSector)) return null;

            int attribute = BinaryPrimitives.ReadUInt16LittleEndian(record0.AsSpan(0x14));
            var hasAttributeList = false;
            while (attribute + 8 <= record0.Length)
            {
                var type = BinaryPrimitives.ReadUInt32LittleEndian(record0.AsSpan(attribute));
                if (type == 0xFFFFFFFF) break;
                var length = BinaryPrimitives.ReadUInt32LittleEndian(record0.AsSpan(attribute + 4));
                if (length < 0x18 || attribute + length > record0.Length) return null;

                if (type == 0x20) hasAttributeList = true;
                if (type == 0x80 && record0[attribute + 9] == 0)   //the unnamed $DATA attribute
                {
                    if (record0[attribute + 8] != 1) return null;   //a resident $MFT cannot be
                    var dataSize = BinaryPrimitives.ReadInt64LittleEndian(record0.AsSpan(attribute + 0x30));
                    var runOffset = BinaryPrimitives.ReadUInt16LittleEndian(record0.AsSpan(attribute + 0x20));
                    var runs = DecodeRuns(record0, attribute + runOffset, (int)(attribute + length), clusterSize);
                    if (runs == null || runs.Count == 0 || dataSize <= 0) return null;

                    Log.Debug($"{name} NTFS: MFT of {dataSize / recordSize:N0} records ({recordSize} B each) in {runs.Count} run(s); empty records stamped with an update sequence number will be presented as empty.");
                    return new MftLayout((int)recordSize, dataSize / recordSize, runs);
                }
                attribute += (int)length;
            }

            //7-Zip reads the MFT from record 0's own $DATA runs too, so an MFT so fragmented that its runs
            //continue in an attribute list is beyond both of us
            Log.Debug($"{name} NTFS: MFT record 0 holds no $DATA{(hasAttributeList ? " (it has an attribute list)" : "")}; serving the image unchanged.");
            return null;
        }

        //restores the last two bytes of each sector from the update sequence array; false if the record is not stamped consistently
        static bool ApplyFixup(byte[] record, int bytesPerSector)
        {
            var arrayOffset = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(4));
            var count = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(6));
            if (count == 0 || count - 1 != record.Length / bytesPerSector || arrayOffset + 2 * count > record.Length) return false;

            var usn = BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(arrayOffset));
            for (var i = 1; i < count; i++)
            {
                var tail = i * bytesPerSector - 2;
                if (BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(tail)) != usn) return false;
                record[tail] = record[arrayOffset + 2 * i];
                record[tail + 1] = record[arrayOffset + 2 * i + 1];
            }
            return true;
        }

        //an NTFS run list: each entry is a header byte (low nibble: length field size, high nibble: offset field size),
        //a cluster count and a signed cluster delta from the previous run
        static List<MftRun>? DecodeRuns(byte[] record, int p, int end, long clusterSize)
        {
            var runs = new List<MftRun>();
            long lcn = 0, logical = 0;
            while (p < end && record[p] != 0)
            {
                var lengthSize = record[p] & 0xF;
                var offsetSize = record[p] >> 4;
                p++;
                if (lengthSize == 0 || lengthSize > 8 || offsetSize == 0 || offsetSize > 8 || p + lengthSize + offsetSize > end) return null;

                long length = 0;
                for (var i = lengthSize - 1; i >= 0; i--) length = (length << 8) | record[p + i];
                p += lengthSize;

                long delta = (sbyte)record[p + offsetSize - 1];   //sign-extend from the top byte
                for (var i = offsetSize - 2; i >= 0; i--) delta = (delta << 8) | record[p + i];
                p += offsetSize;

                lcn += delta;
                if (length <= 0 || lcn < 0) return null;
                runs.Add(new MftRun(logical, lcn * clusterSize, length * clusterSize));
                logical += length * clusterSize;
            }
            return runs;
        }

        //----- Stream -----

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => position;
            set => position = value;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = ReadAt(position, buffer, offset, count);
            position += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => position + offset,
                SeekOrigin.End => Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            return position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
