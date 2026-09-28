using libBzip2;
using libClonezilla.Cache;
using libCommon;
using libCommon.Streams;
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

            //decompressConcatenated: true. Clonezilla's parallel-bzip2 option (pbzip2/lbzip2) writes many
            //concatenated bzip2 streams into one file; with this false, decoding stopped after the first
            //stream and produced a truncated image (extract-partition-image feeds through this sequential
            //path). It is a no-op for a single-stream file - decoding stops cleanly at EOF (gh review F5).
            var result = BZip2Stream.Create(new NonClosingStream(CompressedStream), SharpCompress.Compressors.CompressionMode.Decompress, true);

            return result;
        }
    }
}
