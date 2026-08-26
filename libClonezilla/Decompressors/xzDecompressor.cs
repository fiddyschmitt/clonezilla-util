using libClonezilla.Cache;
using libCommon;
using libCommon.Streams;
using Serilog;
using SharpCompress.Compressors.Xz;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using XzSeekable;

namespace libClonezilla.Decompressors
{
#pragma warning disable IDE1006 // Naming Styles
    public class xzDecompressor : Decompressor
#pragma warning restore IDE1006 // Naming Styles
    {
        public IPartitionCache? PartitionCache { get; }

        public xzDecompressor(Stream compressedStream, IPartitionCache? partitionCache = null) : base(compressedStream)
        {
            PartitionCache = partitionCache;
        }

        public override Stream? GetSeekableStream()
        {
            //Multi-block xz (xz -T / pixz drive images) carries a native block index in its footer, so
            //random access is free - no index build. Single-block xz (Clonezilla -z5 partitions) has
            //no usable native index; if we have somewhere to keep one, build an LZMA2-chunk checkpoint
            //index instead of extracting the whole partition to cache.train.
            try
            {
                CompressedStream.Seek(0, SeekOrigin.Begin);
                var indexed = XzBlockIndexedStream.Open(CompressedStream, leaveOpen: true);
                Log.Information($"xz: serving random access from the native block index ({indexed.Container.BlockCount} blocks).");
                return new SeekableXzStream(indexed, indexed.GetRecommendation, indexed.CreateView);
            }
            catch (XzFormatException)
            {
                //single-block: no native index. Build the checkpoint index if we have a cache folder.
                return GetSeekableStreamViaCheckpointIndex();
            }
            catch (Exception ex)
            {
                Log.Warning($"xz: could not open the native block index ({ex.Message}). Falling back to extraction.");
                return null;
            }
        }

        Stream? GetSeekableStreamViaCheckpointIndex()
        {
            if (PartitionCache == null)
            {
                return null;   //nowhere to persist an index -> extraction fallback
            }

            try
            {
                var indexFilename = PartitionCache.GetXzIndexFilename();
                if (!File.Exists(indexFilename))
                {
                    Log.Information($"Creating xz random-access index: {Path.GetFileName(indexFilename)}");
                }

                //Denser checkpoints than the package default (32 MB): a scattered random read must
                //decode-and-discard from the nearest checkpoint, so with 32 MB spacing a 4 KB read cost
                //up to ~32 MB of decode - the reason single-block xz was by far the slowest codec for
                //fragmented files (measured ~615s for a 3.6 MB log vs bzip2's ~274s). 16 MB checkpoints
                //roughly halve the per-read decode. But each checkpoint stores a (DEFLATE-compressed) LZMA
                //dictionary window, so denser spacing costs index SIZE super-linearly: 8 MB checkpoints
                //ballooned sda2's index to ~10 GB; 16 MB keeps it to a few GB - the balance we want until
                //the window payloads are compressed better (e.g. zstd long-range) in the XzSeekable package.
                //Paired with the ".xz_index_v2" filename so existing 32 MB indexes rebuild at this density.
                var options = new XzIndexOptions { Logger = new SerilogLoggerBridge(), TargetSpanBytes = 16L * 1024 * 1024 };
                CompressedStream.Seek(0, SeekOrigin.Begin);
                var index = XzIndex.LoadOrBuild(CompressedStream, indexFilename, options, new XzBuildProgressLogger());

                //serving-quality gate (mirrors ZstdDecompressor): if points are somehow too sparse to
                //serve efficiently, prefer the extraction fallback. For single-block xz this never
                //fires (LZMA2 chunks are <=2 MB, so points land ~every TargetSpanBytes).
                var maxGap = MaxPointGap(index);
                if (maxGap > 4 * options.TargetSpanBytes)
                {
                    Log.Warning($"xz index for {Path.GetFileName(indexFilename)} is too sparse to serve efficiently (largest gap {maxGap.BytesToString()}). Extracting instead.");
                    index.Dispose();
                    return null;
                }

                var stream = new XzIndexedStream(CompressedStream, index, leaveOpen: true);
                return new SeekableXzStream(stream, start =>
                {
                    var r = stream.GetRecommendation(start);
                    return (r.Start, r.End);
                }, stream.CreateView);
            }
            catch (Exception ex)
            {
                Log.Warning($"xz: could not build a checkpoint index ({ex.Message}). Falling back to extraction.");
                return null;
            }
        }

        static long MaxPointGap(XzIndex index)
        {
            var maxGap = 0L;
            for (var i = 0; i < index.Points.Count; i++)
            {
                var end = i + 1 < index.Points.Count ? index.Points[i + 1].UncompressedOffset : index.UncompressedLength;
                maxGap = Math.Max(maxGap, end - index.Points[i].UncompressedOffset);
            }
            return maxGap;
        }

        public override Stream GetSequentialStream()
        {
            CompressedStream.Seek(0, SeekOrigin.Begin);
            var result = new XZStream(CompressedStream);
            return result;
        }
    }
}
