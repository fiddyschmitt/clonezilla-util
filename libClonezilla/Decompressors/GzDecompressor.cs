using libClonezilla.Cache;
using libCommon.Streams.Seekable;
using libGZip;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libClonezilla.Decompressors
{
    public class GzDecompressor : Decompressor
    {
        public GzDecompressor(Stream compressedStream, IPartitionCache? partitionCache) : base(compressedStream)
        {
            PartitionCache = partitionCache;
        }

        public IPartitionCache? PartitionCache { get; }

        public override Stream? GetSeekableStream()
        {
            if (PartitionCache == null)
            {
                return null;
            }
            else
            {
                //this is faster for random seeks (eg. serving the full image (or file contents) in a Virtual File System)
                //Uses gztool to create an index for fast seek, plus a cache layer to avoid using gztool for small reads

                var gztoolIndexFilename = PartitionCache.GetGztoolIndexFilename();
                var tempgztoolIndexFilename = gztoolIndexFilename + ".wip";

                var gzipStreamSeekable = new GZipStreamSeekable(CompressedStream, tempgztoolIndexFilename, gztoolIndexFilename);

                return gzipStreamSeekable;
            }
        }

        public override Stream GetSequentialStream()
        {
            CompressedStream.Seek(0, SeekOrigin.Begin);
            var uncompressedStream = new GZipStream(CompressedStream, CompressionMode.Decompress);
            return uncompressedStream;
        }
    }
}
