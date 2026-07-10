using libCommon;
using Serilog;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using ZstdSharp.Unsafe;

namespace libZstd
{
    /// <summary>
    /// One resume point in a zstd stream. Unlike gzip/bzip2, a zstd block can depend on decoder state
    /// beyond the content window (repeat offsets and entropy tables carried from earlier blocks), so a
    /// block boundary is only usable as a resume point if a resumed decode is bit-identical to the
    /// true decode - an empirical property of the encoder's output (~88% of boundaries on real
    /// Clonezilla data). Points in this index are therefore VERIFIED: a shadow decoder resumed at the
    /// point runs alongside the true decode for the point's whole span before the point is written.
    /// Readers must never serve bytes beyond a point's span from that point's resume (only output
    /// *within* the verified span is guaranteed, not decoder state at its end).
    /// </summary>
    public sealed class ZstdIndexPoint
    {
        public long UncompressedOffset;
        public long CompressedOffset;
        public bool IsFrameStart;           //resume = decode the real frame header at CompressedOffset; no prefix needed
        public byte WindowDescriptor;       //window-descriptor byte for the synthetic frame header (mid-frame points)
        public long WindowPositionInFile;   //where this point's zstd-compressed window sits in the index file
        public int WindowCompressedLength;  //0 = no window (frame starts)
    }

    /// <summary>
    /// Random-access index over a standard (non-seekable-format) zstd stream: verified resume points
    /// every ~<see cref="TargetSpanBytes"/> of output, each carrying the preceding ≤windowSize of
    /// content (zstd-compressed in the index file, loaded lazily).
    ///
    /// Built in a SINGLE sequential pass: at any moment two shadow decoders run alongside the true
    /// decode - one for the last confirmed point (insurance covering its still-open span) and one for
    /// the current candidate. A candidate that survives a full span byte-identical is confirmed,
    /// which seals its predecessor (span fully verified by the insurance shadow) and appends it to
    /// the index file immediately. A diverging candidate is simply re-armed at a later boundary (the
    /// insurance shadow keeps the open span covered). A diverging CONFIRMED shadow - divergence
    /// deeper than a whole span, never yet observed on real data - aborts indexing and the caller
    /// falls back to extraction: never wrong data, at worst no index.
    ///
    /// The file grows incrementally (gztool-style: header counts zeroed until finalisation), so an
    /// interrupted build RESUMES: sealed points are kept, and the build fast-forwards from the last
    /// sealed frame-start point (a frame-start resume IS the true decode, so the truth chain stays
    /// rooted; zstd points carry window-only state, unlike gzip's complete checkpoint state, which is
    /// why resume cannot simply continue from the last sealed point). All integers little-endian.
    /// </summary>
    public sealed class ZstdSeekableIndex
    {
        //~64 MB spans balance cold-seek decode cost against index size (one ≤2 MB window snapshot per
        //point, zstd-compressed; windows over sparse regions shrink to almost nothing)
        public const long TargetSpanBytes = 64L * 1024 * 1024;
        const int WindowCompressionLevel = 3;
        const long ProgressIntervalBytes = 1L * 1024 * 1024 * 1024;    //log build progress every 1 GB of output
        static readonly byte[] Magic = "ZSTZRAN2"u8.ToArray();
        const int HeaderSize = 8 + 8 + 4;   //magic, totalUncompressed, pointCount
        const int PointFixedSize = 8 + 8 + 1 + 1 + 4;

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
            var (loadedPoints, totalLength, complete) = ReadFile(fs, tolerateTruncatedTail: false);
            if (!complete) throw new InvalidDataException("zstd index was not finalised.");

            return new ZstdSeekableIndex { Filename = indexFilename, points = loadedPoints, UncompressedTotalLength = totalLength };
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

        //================================================================= build (single pass, incremental)

        public static unsafe ZstdSeekableIndex Build(Stream compressedStream, string indexFilename)
        {
            var wipFilename = indexFilename + ".wip";

            //an interrupted earlier build leaves a .wip whose sealed points are all fully verified -
            //keep them and continue from where it stopped
            List<ZstdIndexPoint> sealedPoints = [];
            if (File.Exists(wipFilename))
            {
                try
                {
                    using var existing = File.OpenRead(wipFilename);
                    (sealedPoints, _, _) = ReadFile(existing, tolerateTruncatedTail: true);
                }
                catch (Exception ex)
                {
                    Log.Warning($"Could not read partial zstd index ({ex.Message}). Starting fresh.");
                    sealedPoints = [];
                }
            }

            var resuming = sealedPoints.Count > 0;
            Log.Information(resuming
                ? $"Resuming zstd random-access index ({sealedPoints.Count:N0} verified points already on disk): {Path.GetFileName(indexFilename)}"
                : $"Creating zstd random-access index: {Path.GetFileName(indexFilename)}");

            using var wip = new FileStream(wipFilename, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            if (resuming)
            {
                //keep complete records only; truncate any partial tail from the interruption
                var lastComplete = sealedPoints[^1].WindowPositionInFile + sealedPoints[^1].WindowCompressedLength;
                wip.SetLength(lastComplete);
                wip.Position = lastComplete;
            }
            else
            {
                wip.SetLength(0);
                Span<byte> header = stackalloc byte[HeaderSize];
                Magic.CopyTo(header);
                BinaryPrimitives.WriteInt64LittleEndian(header[8..], 0);    //totalUncompressed: unknown while growing
                BinaryPrimitives.WriteInt32LittleEndian(header[16..], 0);   //pointCount: 0 marks the index incomplete
                wip.Write(header);
                wip.Flush();
            }

            var reader = new BlockReader(compressedStream);
            using var windowCompressor = new ZstdSharp.Compressor(WindowCompressionLevel);

            var main = Methods.ZSTD_createDCtx();
            Methods.ZSTD_DCtx_setParameter(main, ZSTD_dParameter.ZSTD_d_windowLogMax, 31);

            //the two in-flight points and their shadows
            ZstdIndexPoint? confirmed = null;       //last confirmed point; its span is still open
            byte[] confirmedWindowRaw = [];
            ShadowDecoder? confirmedShadow = null;  //null for frame-start points (exact by construction)
            var confirmedAlreadyWritten = false;    //true for a re-adopted point after a resume
            ZstdIndexPoint? candidate = null;
            byte[] candidateWindowRaw = [];
            ShadowDecoder? candidateShadow = null;

            try
            {
                var outBuf = new byte[1 << 20];     //one block regenerates ≤128 KB

                long uncompressedPos = 0;
                long nextProgressAt = ProgressIntervalBytes;

                //rolling ring of true output, indexed by absolute position % windowSize
                var ring = Array.Empty<byte>();
                long frameWindowSize = 0;
                byte frameWindowDescriptor = 0;

                //---- resume: fast-forward the true decode from the last sealed frame-start point,
                //then re-adopt the last sealed point as the open confirmed point ----
                long fastForwardUntil = 0;
                var adoptPending = false;
                ZstdIndexPoint? adoptPoint = null;
                if (resuming)
                {
                    var frameStart = sealedPoints.FindLast(p => p.IsFrameStart)!;   //points[0] is always a frame start
                    adoptPoint = sealedPoints[^1];
                    adoptPending = true;
                    fastForwardUntil = adoptPoint.UncompressedOffset;
                    uncompressedPos = frameStart.UncompressedOffset;
                    nextProgressAt = (uncompressedPos / ProgressIntervalBytes + 1) * ProgressIntervalBytes;
                    compressedStream.Seek(frameStart.CompressedOffset, SeekOrigin.Begin);
                    Log.Information($"Fast-forwarding {(fastForwardUntil - uncompressedPos).BytesToString()} to the last verified point (a zstd point cannot checkpoint full decoder state the way gzip's can).");
                }
                else
                {
                    compressedStream.Seek(0, SeekOrigin.Begin);
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

                void SealConfirmed()
                {
                    if (confirmed == null) return;
                    if (!confirmedAlreadyWritten)
                    {
                        var windowCompressed = confirmedWindowRaw.Length == 0 ? [] : windowCompressor.Wrap(confirmedWindowRaw).ToArray();
                        AppendPoint(wip, confirmed, windowCompressed);
                        sealedPoints.Add(confirmed);
                    }
                    confirmedAlreadyWritten = false;
                    confirmedShadow?.Dispose();
                    confirmedShadow = null;
                    confirmed = null;
                    confirmedWindowRaw = [];
                }

                void DropCandidate()
                {
                    candidateShadow?.Dispose();
                    candidateShadow = null;
                    candidate = null;
                    candidateWindowRaw = [];
                }

                //candidate survived its whole span: it becomes the confirmed point (sealing its predecessor)
                void PromoteCandidate()
                {
                    SealConfirmed();
                    confirmed = candidate;
                    confirmedWindowRaw = candidateWindowRaw;
                    confirmedShadow = candidateShadow;
                    candidate = null;
                    candidateWindowRaw = [];
                    candidateShadow = null;
                }

                while (true)
                {
                    var frameStartOffset = reader.Position;
                    var frameKind = reader.BeginFrame(out var headerBytes, out frameWindowSize, out frameWindowDescriptor, out var hasChecksum);
                    if (frameKind == FrameKind.EndOfStream) break;
                    if (frameKind == FrameKind.Skippable) continue;

                    if (frameWindowSize > 1L << 30) throw new InvalidDataException($"zstd window {frameWindowSize:N0} too large to index.");
                    if (ring.Length < frameWindowSize) ring = new byte[frameWindowSize];

                    var fastForwarding = uncompressedPos < fastForwardUntil;

                    //a frame start is a perfect (stateless) resume point: confirm it immediately
                    if (!fastForwarding)
                    {
                        if (adoptPending)
                        {
                            //the adoption point can only coincide with a frame start here (a mid-frame
                            //adoption happens at a block boundary inside the loop below)
                            if (uncompressedPos != adoptPoint!.UncompressedOffset || frameStartOffset != adoptPoint.CompressedOffset)
                                throw new InvalidDataException("Compressed stream does not match the partial index (resume position not found).");

                            confirmed = adoptPoint;
                            confirmedAlreadyWritten = true;
                            confirmedWindowRaw = [];
                            confirmedShadow = null;     //frame start: exact by construction
                            adoptPending = false;
                        }
                        else
                        {
                            SealConfirmed();
                            confirmed = new ZstdIndexPoint
                            {
                                UncompressedOffset = uncompressedPos,
                                CompressedOffset = frameStartOffset,
                                IsFrameStart = true,
                                WindowDescriptor = frameWindowDescriptor,
                            };
                            confirmedWindowRaw = [];
                            confirmedShadow = null;     //exact by construction
                        }
                    }

                    if (!Feed(main, headerBytes, outBuf, out _)) throw new InvalidDataException("zstd frame header rejected.");

                    var lastBlock = false;
                    while (!lastBlock)
                    {
                        var boundaryCompressedOffset = reader.Position;
                        fastForwarding = uncompressedPos < fastForwardUntil;

                        if (!fastForwarding)
                        {
                            if (adoptPending)
                            {
                                //fast-forward complete: re-adopt the last sealed point as the open
                                //confirmed point, with a fresh insurance shadow from the rebuilt ring
                                if (uncompressedPos != adoptPoint!.UncompressedOffset || boundaryCompressedOffset != adoptPoint.CompressedOffset)
                                    throw new InvalidDataException("Compressed stream does not match the partial index (resume position not found).");

                                confirmed = adoptPoint;
                                confirmedAlreadyWritten = true;
                                confirmedWindowRaw = SnapshotWindow();
                                confirmedShadow = new ShadowDecoder(confirmedWindowRaw, adoptPoint.WindowDescriptor);
                                if (!confirmedShadow.Healthy) throw new InvalidDataException("Could not re-establish the resume point's shadow decoder.");
                                adoptPending = false;
                            }

                            //confirm the candidate once it has survived one whole span byte-identical
                            if (candidate != null && uncompressedPos >= candidate.UncompressedOffset + TargetSpanBytes)
                            {
                                PromoteCandidate();
                            }

                            //arm a new candidate a span past the confirmed point
                            if (candidate == null && confirmed != null && uncompressedPos >= confirmed.UncompressedOffset + TargetSpanBytes)
                            {
                                candidateWindowRaw = SnapshotWindow();
                                candidate = new ZstdIndexPoint
                                {
                                    UncompressedOffset = uncompressedPos,
                                    CompressedOffset = boundaryCompressedOffset,
                                    IsFrameStart = false,
                                    WindowDescriptor = frameWindowDescriptor,
                                };
                                candidateShadow = new ShadowDecoder(candidateWindowRaw, frameWindowDescriptor);
                                if (!candidateShadow.Healthy) DropCandidate();
                            }
                        }

                        var block = reader.ReadBlock(out lastBlock);

                        if (!Feed(main, block, outBuf, out var produced)) throw new InvalidDataException("zstd decode error during index build.");
                        var truth = outBuf.AsSpan(0, produced);

                        //rolling window ring
                        for (var copied = 0; copied < produced;)
                        {
                            var ringPos = (int)((uncompressedPos + copied) % frameWindowSize);
                            var n = (int)Math.Min(produced - copied, frameWindowSize - ringPos);
                            Array.Copy(outBuf, copied, ring, ringPos, n);
                            copied += n;
                        }

                        if (!fastForwarding)
                        {
                            //insurance shadow: covers the confirmed point's still-open span. A mismatch
                            //here means a divergence DEEPER than a whole span (never observed on real
                            //data) - abort; the caller falls back to extraction. Never wrong data.
                            if (confirmedShadow != null && !confirmedShadow.FeedAndCompare(block, truth))
                            {
                                throw new InvalidDataException($"zstd resume state diverged deeper than a span at {uncompressedPos:N0}.");
                            }

                            //candidate shadow: divergence is normal (~12% of boundaries) - re-arm later
                            if (candidateShadow != null && !candidateShadow.FeedAndCompare(block, truth))
                            {
                                DropCandidate();
                            }
                        }

                        uncompressedPos += produced;

                        if (uncompressedPos >= nextProgressAt)
                        {
                            var percentThroughCompressedSource = (double)reader.Position / compressedStream.Length * 100;
                            Log.Information($"Indexed {uncompressedPos.BytesToString()}. ({percentThroughCompressedSource:N1}% through source file)");
                            nextProgressAt = (uncompressedPos / ProgressIntervalBytes + 1) * ProgressIntervalBytes;
                        }
                    }

                    reader.EndFrame(hasChecksum);

                    //frame end: a live candidate has verified [candidate -> frame end] = its whole
                    //actual span (the next point will be at or after the next frame's start)
                    if (candidate != null)
                    {
                        PromoteCandidate();
                    }
                }

                if (adoptPending) throw new InvalidDataException("Compressed stream ended before the previously indexed position.");
                SealConfirmed();

                if (sealedPoints.Count == 0) throw new InvalidDataException("No zstd frames found.");

                FinaliseFile(wip, uncompressedPos, sealedPoints.Count);
                wip.Dispose();
                File.Move(wipFilename, indexFilename, overwrite: true);

                Log.Information($"Finished creating zstd index: {Path.GetFileName(indexFilename)} ({sealedPoints.Count:N0} verified points)");
                return new ZstdSeekableIndex
                {
                    Filename = indexFilename,
                    points = sealedPoints,
                    UncompressedTotalLength = uncompressedPos,
                };
            }
            finally
            {
                Methods.ZSTD_freeDCtx(main);
                confirmedShadow?.Dispose();
                candidateShadow?.Dispose();
            }
        }

        //================================================================= file format

        static (List<ZstdIndexPoint> Points, long TotalLength, bool Complete) ReadFile(FileStream fs, bool tolerateTruncatedTail)
        {
            Span<byte> header = stackalloc byte[HeaderSize];
            fs.ReadExactly(header);
            if (!header[..8].SequenceEqual(Magic)) throw new InvalidDataException("Not a zstd zran index.");
            var totalLength = BinaryPrimitives.ReadInt64LittleEndian(header[8..]);
            var count = BinaryPrimitives.ReadInt32LittleEndian(header[16..]);
            var complete = count > 0;

            var result = new List<ZstdIndexPoint>();
            Span<byte> record = stackalloc byte[PointFixedSize];
            while (complete ? result.Count < count : true)
            {
                var recordStart = fs.Position;
                var got = fs.ReadAtLeast(record, PointFixedSize, throwOnEndOfStream: false);
                if (got < PointFixedSize)
                {
                    if (complete || (!tolerateTruncatedTail && got != 0)) throw new InvalidDataException("zstd index truncated.");
                    break;
                }

                var point = new ZstdIndexPoint
                {
                    UncompressedOffset = BinaryPrimitives.ReadInt64LittleEndian(record),
                    CompressedOffset = BinaryPrimitives.ReadInt64LittleEndian(record[8..]),
                    IsFrameStart = record[16] != 0,
                    WindowDescriptor = record[17],
                    WindowCompressedLength = BinaryPrimitives.ReadInt32LittleEndian(record[18..]),
                    WindowPositionInFile = recordStart + PointFixedSize,
                };

                if (point.WindowCompressedLength < 0 || point.UncompressedOffset < 0 || point.CompressedOffset < 0)
                {
                    if (tolerateTruncatedTail && !complete) break;
                    throw new InvalidDataException("zstd index point has implausible values.");
                }

                if (fs.Position + point.WindowCompressedLength > fs.Length)
                {
                    if (tolerateTruncatedTail && !complete) break;
                    throw new InvalidDataException("zstd index truncated inside a window.");
                }
                fs.Seek(point.WindowCompressedLength, SeekOrigin.Current);

                result.Add(point);
            }

            return (result, totalLength, complete);
        }

        static void AppendPoint(FileStream fs, ZstdIndexPoint point, byte[] windowCompressed)
        {
            Span<byte> record = stackalloc byte[PointFixedSize];
            BinaryPrimitives.WriteInt64LittleEndian(record, point.UncompressedOffset);
            BinaryPrimitives.WriteInt64LittleEndian(record[8..], point.CompressedOffset);
            record[16] = point.IsFrameStart ? (byte)1 : (byte)0;
            record[17] = point.WindowDescriptor;
            BinaryPrimitives.WriteInt32LittleEndian(record[18..], windowCompressed.Length);

            fs.Write(record);
            point.WindowPositionInFile = fs.Position;
            point.WindowCompressedLength = windowCompressed.Length;
            fs.Write(windowCompressed);
            fs.Flush();     //each sealed point survives an interruption
        }

        static void FinaliseFile(FileStream fs, long totalLength, int count)
        {
            fs.Position = 8;
            Span<byte> tail = stackalloc byte[12];
            BinaryPrimitives.WriteInt64LittleEndian(tail, totalLength);
            BinaryPrimitives.WriteInt32LittleEndian(tail[8..], count);
            fs.Write(tail);
            fs.Flush();
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

        /// <summary>
        /// A decoder resumed at a candidate point (window preloaded via refPrefix, synthetic frame
        /// header), fed the same blocks as the true decode and byte-compared against it. The window
        /// array is pinned for the shadow's lifetime (refPrefix references it directly).
        /// </summary>
        sealed unsafe class ShadowDecoder : IDisposable
        {
            ZSTD_DCtx_s* dctx;
            GCHandle windowPin;
            readonly byte[] scratch = new byte[1 << 20];

            public bool Healthy { get; }

            public ShadowDecoder(byte[] windowRaw, byte windowDescriptor)
            {
                dctx = Methods.ZSTD_createDCtx();
                Methods.ZSTD_DCtx_setParameter(dctx, ZSTD_dParameter.ZSTD_d_windowLogMax, 31);

                var ok = true;
                if (windowRaw.Length > 0)
                {
                    windowPin = GCHandle.Alloc(windowRaw, GCHandleType.Pinned);
                    var r = Methods.ZSTD_DCtx_refPrefix(dctx, (byte*)windowPin.AddrOfPinnedObject(), (nuint)windowRaw.Length);
                    ok = !Methods.ZSTD_isError(r);
                }
                if (ok) ok = Feed(dctx, SyntheticFrameHeader(windowDescriptor), scratch, out _);
                Healthy = ok;
            }

            public bool FeedAndCompare(ReadOnlySpan<byte> block, ReadOnlySpan<byte> truth)
            {
                if (!Feed(dctx, block, scratch, out var produced)) return false;
                return produced == truth.Length && scratch.AsSpan(0, produced).SequenceEqual(truth);
            }

            public void Dispose()
            {
                if (dctx != null)
                {
                    Methods.ZSTD_freeDCtx(dctx);
                    dctx = null;
                }
                if (windowPin.IsAllocated) windowPin.Free();
            }
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
