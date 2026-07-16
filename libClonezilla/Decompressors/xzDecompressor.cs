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
        public xzDecompressor(Stream compressedStream) : base(compressedStream)
        {

        }

        public override Stream? GetSeekableStream()
        {
            //Multi-block xz (e.g. xz -T / pixz drive images) carries a native block index in its
            //footer, so random access is free - no index build. Single-block xz (Clonezilla -z5
            //partitions) has no usable index; XzBlockIndexedStream.Open throws and we fall through to
            //the extraction path (until the single-block checkpoint index lands).
            try
            {
                CompressedStream.Seek(0, SeekOrigin.Begin);
                var indexed = XzBlockIndexedStream.Open(CompressedStream, leaveOpen: true);
                Log.Information($"xz: serving random access from the native block index ({indexed.Container.BlockCount} blocks).");
                return new SeekableXzStream(indexed);
            }
            catch (XzFormatException)
            {
                //single-block, or framing we don't handle - use the extraction fallback
                return null;
            }
            catch (Exception ex)
            {
                Log.Warning($"xz: could not open the native block index ({ex.Message}). Falling back to extraction.");
                return null;
            }
        }

        public override Stream GetSequentialStream()
        {
            CompressedStream.Seek(0, SeekOrigin.Begin);
            var result = new XZStream(CompressedStream);
            return result;
        }
    }
}
