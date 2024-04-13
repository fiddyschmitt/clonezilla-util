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
    public class Bzip2Decompressor(Stream compressedStream, IPartitionCache? partitionCache) : Decompressor(compressedStream)
    {
        public IPartitionCache? PartitionCache { get; } = partitionCache;

        public override Stream? GetSeekableStream()
        {
            if (PartitionCache == null)
            {
                return null;
            }
            else
            {
                //todo
                //var gztoolIndexFilename = PartitionCache.GetGztoolIndexFilename();

                var seekableStream = new Bzip2StreamSeekable(CompressedStream, "");

                return seekableStream;
            }
        }

        public override Stream GetSequentialStream()
        {
            CompressedStream.Seek(0, SeekOrigin.Begin);
            var result = new BZip2Stream(CompressedStream, SharpCompress.Compressors.CompressionMode.Decompress, false);

            return result;
        }
    }
}
