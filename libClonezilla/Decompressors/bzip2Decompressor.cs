using libBzip2;
using libClonezilla.Cache;
using libCommon;
using libGZip;
using Serilog;
using SharpCompress.Compressors.BZip2;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libClonezilla.Decompressors
{
    public class Bzip2Decompressor(Stream compressedStream, IPartitionCache? partitionCache, bool processTrailingNulls) : Decompressor(compressedStream)
    {
        public IPartitionCache? PartitionCache { get; } = partitionCache;
        public bool ProcessTrailingNulls { get; } = processTrailingNulls;

        public override Stream? GetSeekableStream()
        {
            var indexFilename = PartitionCache?.GetBZip2IndexFilename();

            var seekableStream = new Bzip2StreamSeekable(CompressedStream, indexFilename, ProcessTrailingNulls);

            return seekableStream;
        }

        public override Stream GetSequentialStream()
        {
            CompressedStream.Seek(0, SeekOrigin.Begin);
            var result = new BZip2Stream(CompressedStream, SharpCompress.Compressors.CompressionMode.Decompress, false);

            return result;
        }
    }
}
