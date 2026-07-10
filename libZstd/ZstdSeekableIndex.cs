using Serilog;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using ZstdSharp.Unsafe;

namespace libZstd
{
    /// <summary>
    /// One resume point in a zstd stream. Unlike gzip/bzip2, a zstd block can depend on decoder state
    /// beyond the content window (repeat offsets and entropy tables carried from earlier blocks), so a
    /// block boundary is only usable as a resume point if a resumed decode is bit-identical to the
    /// true decode - an empirical property of the encoder's output (~88% of boundaries on real
    /// Clonezilla data). Points in this index are therefore VERIFIED: trial-decoded during the build
    /// and byte-checked across their whole span. Readers must never serve bytes beyond a point's span
    /// from that point's resume (only output *within* the verified span is guaranteed, not decoder
    /// state at its end).
    /// </summary>
    public sealed class ZstdIndexPoint
    {
        public long UncompressedOffset;
        public long CompressedOffset;
        public bool IsFrameStart;           //resume = decode the real frame header at CompressedOffset; no prefix needed
        public byte WindowDescriptor;       //window-descriptor byte for the synthetic frame header (mid-frame points)
        public byte[] SpanMd5 = [];         //MD5 of the uncompressed span [this point, next point)
        public long WindowPositionInFile;   //where this point's zstd-compressed window sits in the index file
        public int WindowCompressedLength;  //0 = no window (frame starts)
    }

    /// <summary>
    /// Random-access index over a standard (non-seekable-format) zstd stream: verified resume points
    /// every ~<see cref="TargetSpanBytes"/> of output, each carrying the preceding ≤windowSize of
    /// content (zstd-compressed in the index file, loaded lazily). Built by one sequential decode with
    /// inline trial-validation of every candidate point, then a parallel whole-span verification pass;
    /// a stream whose spans cannot all be verified yields no index (callers fall back to extraction).
    /// All integers little-endian.
    /// </summary>
    public sealed class ZstdSeekableIndex
    {
        //~64 MB spans balance cold-seek decode cost against index size (one ≤2 MB window snapshot per
        //point, zstd-compressed; windows over sparse regions shrink to almost nothing)
        public const long TargetSpanBytes = 64L * 1024 * 1024;
        const int TrialLookaheadBytes = 4 * 1024 * 1024;    //inline candidate validation depth (the verify pass then covers full spans)
        const int WindowCompressionLevel = 3;
        static readonly byte[] Magic = "ZSTZRAN1"u8.ToArray();

        public string Filename { get; private set; } = "";
        public IReadOnlyList<ZstdIndexPoint> Points => points;
        public long UncompressedTotalLength { get; private set; }

        List<ZstdIndexPoint> points = [];

        public static ZstdSeekableIndex? LoadOrCreate(Stream compressedStream, string indexFilename)
        {
            if (File.Exists(indexFilename))
            {
                try
                {
                    return Load(indexFilename);
                }
                catch (Exception ex)
                {
                    Log.Warning($"Could not load zstd index {Path.GetFileName(indexFilename)} ({ex.Message}). Rebuilding.");
                }
            }

            try
            {
                return Build(compressedStream, indexFilename);
            }
            catch (Exception ex)
            {
                Log.Warning($"Could not build a zstd random-access index ({ex.Message}). Falling back to extraction.");
                return null;
            }
        }

        public static ZstdSeekableIndex Load(string indexFilename)
        {
            using var fs = File.OpenRead(indexFilename);
            using var br = new BinaryReader(fs);

            if (!br.ReadBytes(8).SequenceEqual(Magic)) throw new InvalidDataException("Not a zstd zran index.");
            var totalLength = br.ReadInt64();
            var count = br.ReadInt32();
            if (count <= 0) throw new InvalidDataException("zstd index has no points.");

            var loaded = new List<ZstdIndexPoint>(count);
            for (var i = 0; i < count; i++)
            {
                loaded.Add(new ZstdIndexPoint
                {
                    UncompressedOffset = br.ReadInt64(),
                    CompressedOffset = br.ReadInt64(),
                    IsFrameStart = br.ReadBoolean(),
                    WindowDescriptor = br.ReadByte(),
                    SpanMd5 = br.ReadBytes(16),
                    WindowPositionInFile = br.ReadInt64(),
                    WindowCompressedLength = br.ReadInt32(),
                });
            }

            return new ZstdSeekableIndex { Filename = indexFilename, points = loaded, UncompressedTotalLength = totalLength };
        }

        /// <summary>Loads and decompresses one point's window. Thread-safe (opens the file per call).</summary>
        public byte[] LoadWindow(ZstdIndexPoint point)
        {
            if (point.WindowCompressedLength == 0) return [];

            byte[] stored;
            using (var fs = File.OpenRead(Filename))
            {
                fs.Position = point.WindowPositionInFile;
                stored = new byte[point.WindowCompressedLength];
                fs.ReadExactly(stored);
            }
            using var decompressor = new ZstdSharp.Decompressor();
            return decompressor.Unwrap(stored).ToArray();
        }

        //================================================================= build

        public static unsafe ZstdSeekableIndex Build(Stream compressedStream, string indexFilename)
        {
            Log.Information($"Creating zstd random-access index: {Path.GetFileName(indexFilename)}");

            var newPoints = new List<ZstdIndexPoint>();
            var windows = new List<byte[]>();       //compressed window per point (parallel to newPoints)

            compressedStream.Seek(0, SeekOrigin.Begin);
            var reader = new BlockReader(compressedStream);
            using var windowCompressor = new ZstdSharp.Compressor(WindowCompressionLevel);

            var main = Methods.ZSTD_createDCtx();
            Methods.ZSTD_DCtx_setParameter(main, ZSTD_dParameter.ZSTD_d_windowLogMax, 31);
            ZSTD_DCtx_s* trial = null;

            try
            {
                var outBuf = new byte[1 << 20];     //one block regenerates ≤128 KB
                var trialBuf = new byte[1 << 20];

                long uncompressedPos = 0;
                long nextCandidateAt = TargetSpanBytes;
                var spanMd5 = MD5.Create();

                //rolling ring of true output (window snapshots), indexed by absolute position % windowSize
                var ring = Array.Empty<byte>();
                long frameWindowSize = 0;
                byte frameWindowDescriptor = 0;

                //at most one candidate under trial at a time. While a trial is active, output hashing
                //is DEFERRED into `pending`: those bytes belong to the candidate's span if it is
                //accepted (its offset precedes them), or to the current span if it is rejected.
                ZstdIndexPoint? candidate = null;
                byte[]? candidateWindowRaw = null;
                var pending = new byte[TrialLookaheadBytes + (1 << 20)];
                var pendingLength = 0;
                long trialCompared = 0;

                void FinishSpanHash()
                {
                    spanMd5.TransformFinalBlock([], 0, 0);
                    if (newPoints.Count > 0) newPoints[^1].SpanMd5 = spanMd5.Hash!;
                    spanMd5.Dispose();
                    spanMd5 = MD5.Create();
                }

                void DropTrial(bool flushPendingIntoCurrentSpan)
                {
                    if (trial != null) { Methods.ZSTD_freeDCtx(trial); trial = null; }
                    candidate = null;
                    candidateWindowRaw = null;
                    if (flushPendingIntoCurrentSpan && pendingLength > 0)
                    {
                        spanMd5.TransformBlock(pending, 0, pendingLength, null, 0);
                    }
                    pendingLength = 0;
                }

                void AcceptCandidate()
                {
                    //everything hashed so far (excluding `pending`) is the PREVIOUS span - exactly up
                    //to the candidate's offset
                    FinishSpanHash();
                    newPoints.Add(candidate!);
                    windows.Add(candidateWindowRaw!.Length == 0 ? [] : windowCompressor.Wrap(candidateWindowRaw).ToArray());
                    nextCandidateAt = candidate!.UncompressedOffset + TargetSpanBytes;

                    //the deferred bytes are the start of the NEW span
                    spanMd5.TransformBlock(pending, 0, pendingLength, null, 0);
                    pendingLength = 0;

                    if (trial != null) { Methods.ZSTD_freeDCtx(trial); trial = null; }
                    candidate = null;
                    candidateWindowRaw = null;
                }

                byte[] SnapshotWindow()
                {
                    var size = (int)Math.Min(uncompressedPos, frameWindowSize);
                    var snapshot = new byte[size];
                    if (size > 0)
                    {
                        var ringPos = (int)(uncompressedPos % frameWindowSize);
                        if (uncompressedPos <= frameWindowSize)
                        {
                            Array.Copy(ring, 0, snapshot, 0, size);
                        }
                        else
                        {
                            Array.Copy(ring, ringPos, snapshot, 0, frameWindowSize - ringPos);
                            Array.Copy(ring, 0, snapshot, frameWindowSize - ringPos, ringPos);
                        }
                    }
                    return snapshot;
                }

                while (true)
                {
                    var frameStartOffset = reader.Position;
                    var frameKind = reader.BeginFrame(out var headerBytes, out frameWindowSize, out frameWindowDescriptor, out var hasChecksum);
                    if (frameKind == FrameKind.EndOfStream) break;
                    if (frameKind == FrameKind.Skippable) continue;

                    if (frameWindowSize > 1L << 30) throw new InvalidDataException($"zstd window {frameWindowSize:N0} too large to index.");
                    if (ring.Length < frameWindowSize) ring = new byte[frameWindowSize];

                    //a frame start is a perfect (stateless) resume point
                    FinishSpanHash();
                    newPoints.Add(new ZstdIndexPoint
                    {
                        UncompressedOffset = uncompressedPos,
                        CompressedOffset = frameStartOffset,
                        IsFrameStart = true,
                        WindowDescriptor = frameWindowDescriptor,
                    });
                    windows.Add([]);
                    nextCandidateAt = uncompressedPos + TargetSpanBytes;

                    if (!Feed(main, headerBytes, outBuf, out _)) throw new InvalidDataException("zstd frame header rejected.");

                    var lastBlock = false;
                    while (!lastBlock)
                    {
                        var boundaryCompressedOffset = reader.Position;

                        if (candidate == null && uncompressedPos >= nextCandidateAt)
                        {
                            //arm a trial at this block boundary
                            candidateWindowRaw = SnapshotWindow();
                            candidate = new ZstdIndexPoint
                            {
                                UncompressedOffset = uncompressedPos,
                                CompressedOffset = boundaryCompressedOffset,
                                IsFrameStart = false,
                                WindowDescriptor = frameWindowDescriptor,
                            };
                            trialCompared = 0;

                            trial = Methods.ZSTD_createDCtx();
                            Methods.ZSTD_DCtx_setParameter(trial, ZSTD_dParameter.ZSTD_d_windowLogMax, 31);
                            var initOk = true;
                            if (candidateWindowRaw.Length > 0)
                            {
                                fixed (byte* windowPtr = candidateWindowRaw)
                                {
                                    var r = Methods.ZSTD_DCtx_refPrefix(trial, windowPtr, (nuint)candidateWindowRaw.Length);
                                    initOk = !Methods.ZSTD_isError(r);
                                }
                            }
                            if (initOk) initOk = Feed(trial, SyntheticFrameHeader(frameWindowDescriptor), trialBuf, out _);
                            if (!initOk) DropTrial(flushPendingIntoCurrentSpan: true);
                        }

                        var block = reader.ReadBlock(out lastBlock);

                        if (!Feed(main, block, outBuf, out var produced)) throw new InvalidDataException("zstd decode error during index build.");

                        //rolling window ring
                        for (var copied = 0; copied < produced;)
                        {
                            var ringPos = (int)((uncompressedPos + copied) % frameWindowSize);
                            var n = (int)Math.Min(produced - copied, frameWindowSize - ringPos);
                            Array.Copy(outBuf, copied, ring, ringPos, n);
                            copied += n;
                        }

                        if (candidate != null && trial != null)
                        {
                            //defer hashing while the trial is undecided; compare the trial decoder's
                            //output for this same block against the truth
                            Array.Copy(outBuf, 0, pending, pendingLength, produced);
                            pendingLength += produced;

                            if (!Feed(trial, block, trialBuf, out var trialProduced)
                                || trialProduced != produced
                                || !outBuf.AsSpan(0, produced).SequenceEqual(trialBuf.AsSpan(0, trialProduced)))
                            {
                                DropTrial(flushPendingIntoCurrentSpan: true);   //not a valid resume point; try a later boundary
                            }
                            else
                            {
                                trialCompared += produced;
                                if (trialCompared >= TrialLookaheadBytes)
                                {
                                    AcceptCandidate();
                                }
                            }
                        }
                        else
                        {
                            spanMd5.TransformBlock(outBuf, 0, produced, null, 0);
                        }

                        uncompressedPos += produced;
                    }

                    reader.EndFrame(hasChecksum);
                    DropTrial(flushPendingIntoCurrentSpan: true);   //an unresolved trial cannot span frames
                }

                FinishSpanHash();
                spanMd5.Dispose();

                if (newPoints.Count == 0) throw new InvalidDataException("No zstd frames found.");

                var index = new ZstdSeekableIndex
                {
                    Filename = indexFilename,
                    points = newPoints,
                    UncompressedTotalLength = uncompressedPos,
                };

                Log.Information($"zstd index: {newPoints.Count:N0} candidate points over {uncompressedPos:N0} bytes. Verifying every span.");
                index.VerifyAndHeal(compressedStream, windows);

                index.Save(indexFilename, windows);
                Log.Information($"Finished creating zstd index: {Path.GetFileName(indexFilename)} ({index.points.Count:N0} verified points)");
                return index;
            }
            finally
            {
                Methods.ZSTD_freeDCtx(main);
                if (trial != null) Methods.ZSTD_freeDCtx(trial);
            }
        }

        /// <summary>
        /// The hard guarantee: re-decode every span from its point exactly as readers will, comparing
        /// the true decode's MD5 piecewise at every candidate boundary inside the span. A point whose
        /// resume diverges anywhere in its span gets DROPPED and its predecessor re-verified over the
        /// merged span (the inline 4 MB trial can be fooled by long stateless stretches - RLE/raw
        /// blocks neither use nor update repeat-offset state, so a divergence can surface much later).
        /// Healing always terminates: a frame-start resume IS the true decode, sound at any depth.
        /// Only points whose full (possibly merged) span verified survive into the index.
        /// </summary>
        void VerifyAndHeal(Stream compressedStream, List<byte[]> compressedWindows)
        {
            var sharedCompressed = new libCommon.Streams.SharedStream(compressedStream);
            var markers = points;                       //every candidate stays a hash boundary
            var alive = points.Select(_ => true).ToArray();

            //returns the marker index (a < failIdx <= b) of the first piecewise-hash mismatch, or -1 if
            //the whole span [markers[a], nextAliveEndOffset) verified
            int VerifySpan(int a, int b /*exclusive end marker index; markers.Count = EOF*/)
            {
                var point = markers[a];
                var endUncompressed = b < markers.Count ? markers[b].UncompressedOffset : UncompressedTotalLength;
                var endCompressed = b < markers.Count ? markers[b].CompressedOffset : compressedStream.Length;

                byte[] window = [];
                if (compressedWindows[a].Length > 0)
                {
                    using var windowDecompressor = new ZstdSharp.Decompressor();
                    window = windowDecompressor.Unwrap(compressedWindows[a]).ToArray();
                }

                var source = sharedCompressed.CreateView();
                using var resume = new ZstdResumeStream(source, point.CompressedOffset, endCompressed, point.IsFrameStart, point.WindowDescriptor, window);

                var buffer = new byte[1 << 20];
                var position = point.UncompressedOffset;
                var intervalIdx = a;    //hash of [markers[j], markers[j+1]) lives in markers[j].SpanMd5
                var md5 = MD5.Create();
                try
                {
                    while (position < endUncompressed)
                    {
                        var intervalEnd = intervalIdx + 1 < markers.Count ? markers[intervalIdx + 1].UncompressedOffset : UncompressedTotalLength;
                        var want = (int)Math.Min(buffer.Length, intervalEnd - position);
                        var n = resume.Read(buffer, 0, want);
                        if (n == 0) return intervalIdx + 1;     //short decode: treat as divergence in this interval
                        md5.TransformBlock(buffer, 0, n, null, 0);
                        position += n;

                        if (position == intervalEnd)
                        {
                            md5.TransformFinalBlock([], 0, 0);
                            var expected = markers[intervalIdx].SpanMd5;
                            if (!md5.Hash!.SequenceEqual(expected)) return intervalIdx + 1;
                            md5.Dispose();
                            md5 = MD5.Create();
                            intervalIdx++;
                        }
                    }
                    return -1;
                }
                finally
                {
                    md5.Dispose();
                }
            }

            //verify all currently-alive spans in parallel; drop failures; repeat for the merged spans
            var toVerify = Enumerable.Range(0, markers.Count).Where(i => alive[i]).ToList();
            var round = 0;
            while (toVerify.Count > 0)
            {
                round++;
                if (round > markers.Count + 2) throw new InvalidDataException("zstd index verification did not converge.");

                var dropped = new List<int>();
                var next = new List<int>();

                //each worker takes a CONTIGUOUS slice of spans, in order: a handful of sequential
                //cursors through the compressed source instead of a random interleave (a seek storm
                //on spinning disks - the verify pass is I/O-bound, not CPU-bound)
                var workerCount = Math.Min(4, Environment.ProcessorCount);
                var ordered = toVerify.OrderBy(x => x).ToList();
                Parallel.For(0, workerCount, w =>
                {
                    var from = w * ordered.Count / workerCount;
                    var to = (w + 1) * ordered.Count / workerCount;
                    for (var idx = from; idx < to; idx++)
                    {
                        var a = ordered[idx];
                        var b = a + 1;
                        while (b < markers.Count && !alive[b]) b++;

                        if (VerifySpan(a, b) >= 0)
                        {
                            lock (dropped) dropped.Add(a);
                        }
                    }
                });

                foreach (var i in dropped.OrderBy(x => x))
                {
                    if (markers[i].IsFrameStart)
                    {
                        //cannot happen (a frame-start resume is the true decode), but never drop one
                        throw new InvalidDataException($"zstd frame-start span failed verification at {markers[i].UncompressedOffset:N0}.");
                    }
                    alive[i] = false;
                    //the nearest earlier alive point now covers a longer span - re-verify it
                    var predecessor = i - 1;
                    while (predecessor >= 0 && !alive[predecessor]) predecessor--;
                    if (predecessor >= 0 && !next.Contains(predecessor)) next.Add(predecessor);
                }

                if (dropped.Count > 0)
                    Log.Debug($"zstd index verification round {round}: dropped {dropped.Count} unsound resume point(s); re-verifying {next.Count} merged span(s).");

                toVerify = next;
            }

            var survivingWindows = new List<byte[]>();
            var surviving = new List<ZstdIndexPoint>();
            for (var i = 0; i < markers.Count; i++)
            {
                if (!alive[i]) continue;
                surviving.Add(markers[i]);
                survivingWindows.Add(compressedWindows[i]);
            }

            if (surviving.Count < markers.Count)
                Log.Information($"zstd index: {markers.Count - surviving.Count:N0} of {markers.Count:N0} candidate points were unsound and removed; {surviving.Count:N0} verified points remain.");

            points = surviving;
            compressedWindows.Clear();
            compressedWindows.AddRange(survivingWindows);
        }

        void Save(string indexFilename, List<byte[]> compressedWindows)
        {
            var tempFilename = indexFilename + ".wip";
            using (var fs = File.Create(tempFilename))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(Magic);
                bw.Write(UncompressedTotalLength);
                bw.Write(points.Count);

                const int recordSize = 8 + 8 + 1 + 1 + 16 + 8 + 4;
                var windowOffset = 8L + 8 + 4 + (long)points.Count * recordSize;
                for (var i = 0; i < points.Count; i++)
                {
                    var p = points[i];
                    p.WindowPositionInFile = windowOffset;
                    p.WindowCompressedLength = compressedWindows[i].Length;
                    windowOffset += p.WindowCompressedLength;

                    bw.Write(p.UncompressedOffset);
                    bw.Write(p.CompressedOffset);
                    bw.Write(p.IsFrameStart);
                    bw.Write(p.WindowDescriptor);
                    bw.Write(p.SpanMd5.Length == 16 ? p.SpanMd5 : new byte[16]);
                    bw.Write(p.WindowPositionInFile);
                    bw.Write(p.WindowCompressedLength);
                }
                foreach (var w in compressedWindows) bw.Write(w);
            }
            File.Move(tempFilename, indexFilename, overwrite: true);
        }

        //================================================================= low-level helpers

        internal static byte[] SyntheticFrameHeader(byte windowDescriptor) =>
            [0x28, 0xB5, 0x2F, 0xFD, 0x00 /*FHD: no flags*/, windowDescriptor];

        /// <summary>Smallest window-descriptor byte whose window is ≥ <paramref name="windowSize"/>.</summary>
        internal static byte DescriptorForWindowSize(long windowSize)
        {
            for (var exponent = 0; exponent <= 31; exponent++)
            {
                var windowBase = 1L << (10 + exponent);
                for (var mantissa = 0; mantissa <= 7; mantissa++)
                {
                    if (windowBase + (windowBase / 8) * mantissa >= windowSize)
                        return (byte)((exponent << 3) | mantissa);
                }
            }
            return 0xF8;
        }

        internal static unsafe bool Feed(ZSTD_DCtx_s* dctx, ReadOnlySpan<byte> data, byte[] outBuf, out int produced)
        {
            produced = 0;
            fixed (byte* inputPtr = data, outputPtr = outBuf)
            {
                var input = new ZSTD_inBuffer_s { src = inputPtr, size = (nuint)data.Length, pos = 0 };
                while (input.pos < input.size)
                {
                    var output = new ZSTD_outBuffer_s { dst = outputPtr + produced, size = (nuint)(outBuf.Length - produced), pos = 0 };
                    var r = Methods.ZSTD_decompressStream(dctx, &output, &input);
                    if (Methods.ZSTD_isError(r)) return false;
                    produced += (int)output.pos;
                    if (output.pos == 0 && r == 0 && input.pos < input.size) break;    //frame ended with input left over
                }
            }
            return true;
        }

        internal enum FrameKind { Zstd, Skippable, EndOfStream }

        /// <summary>Walks zstd frame/block structure on the compressed side (no decoding).</summary>
        internal sealed class BlockReader(Stream stream)
        {
            byte[] blockBuffer = new byte[128 * 1024 + 16];

            public long Position => stream.Position;

            public FrameKind BeginFrame(out byte[] headerBytes, out long windowSize, out byte windowDescriptor, out bool hasChecksum)
            {
                headerBytes = [];
                windowSize = 0;
                windowDescriptor = 0;
                hasChecksum = false;

                var magic = new byte[4];
                var got = stream.ReadAtLeast(magic, 4, throwOnEndOfStream: false);
                if (got == 0) return FrameKind.EndOfStream;
                if (got < 4) throw new InvalidDataException("Truncated zstd stream (magic).");

                var magicValue = BinaryPrimitives.ReadUInt32LittleEndian(magic);
                if (magicValue >= 0x184D2A50 && magicValue <= 0x184D2A5F)
                {
                    var size = new byte[4];
                    stream.ReadExactly(size);
                    stream.Seek(BinaryPrimitives.ReadUInt32LittleEndian(size), SeekOrigin.Current);
                    return FrameKind.Skippable;
                }
                if (magicValue != 0xFD2FB528) throw new InvalidDataException($"Unexpected zstd magic 0x{magicValue:X8}.");

                var frameHeaderDescriptor = ReadByteStrict();
                var singleSegment = (frameHeaderDescriptor & 0x20) != 0;
                hasChecksum = (frameHeaderDescriptor & 0x04) != 0;
                var dictIdBytes = (frameHeaderDescriptor & 0x03) switch { 0 => 0, 1 => 1, 2 => 2, _ => 4 };
                var fcsBytes = (frameHeaderDescriptor >> 6) switch { 0 => singleSegment ? 1 : 0, 1 => 2, 2 => 4, _ => 8 };

                var rest = new byte[(singleSegment ? 0 : 1) + dictIdBytes + fcsBytes];
                if (rest.Length > 0) stream.ReadExactly(rest);

                if (singleSegment)
                {
                    var fcsSpan = rest.AsSpan(rest.Length - fcsBytes);
                    var contentSize = fcsBytes switch
                    {
                        1 => fcsSpan[0],
                        2 => BinaryPrimitives.ReadUInt16LittleEndian(fcsSpan) + 256,
                        4 => BinaryPrimitives.ReadUInt32LittleEndian(fcsSpan),
                        _ => (long)BinaryPrimitives.ReadUInt64LittleEndian(fcsSpan),
                    };
                    windowSize = Math.Max(1024, contentSize);
                    windowDescriptor = DescriptorForWindowSize(windowSize);
                }
                else
                {
                    windowDescriptor = rest[0];
                    var exponent = windowDescriptor >> 3;
                    var mantissa = windowDescriptor & 7;
                    var windowBase = 1L << (10 + exponent);
                    windowSize = windowBase + (windowBase / 8) * mantissa;
                }

                headerBytes = new byte[4 + 1 + rest.Length];
                magic.CopyTo(headerBytes, 0);
                headerBytes[4] = frameHeaderDescriptor;
                rest.CopyTo(headerBytes, 5);
                return FrameKind.Zstd;
            }

            public ReadOnlySpan<byte> ReadBlock(out bool lastBlock)
            {
                var header = new byte[3];
                stream.ReadExactly(header);
                var blockHeader = header[0] | (header[1] << 8) | (header[2] << 16);
                lastBlock = (blockHeader & 1) != 0;
                var blockType = (blockHeader >> 1) & 3;
                if (blockType == 3) throw new InvalidDataException("Reserved zstd block type.");
                var blockSize = blockHeader >> 3;
                var payload = blockType == 1 ? 1 : blockSize;   //an RLE block stores 1 byte and regenerates blockSize

                if (blockBuffer.Length < 3 + payload) blockBuffer = new byte[3 + payload];
                header.CopyTo(blockBuffer, 0);
                stream.ReadExactly(blockBuffer, 3, payload);
                return blockBuffer.AsSpan(0, 3 + payload);
            }

            public void EndFrame(bool hasChecksum)
            {
                if (hasChecksum) stream.Seek(4, SeekOrigin.Current);
            }

            byte ReadByteStrict()
            {
                var b = stream.ReadByte();
                if (b < 0) throw new InvalidDataException("Truncated zstd stream.");
                return (byte)b;
            }
        }
    }
}
