using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libTrainCompress.Compressors
{
    public class xzCompressor : Compressor
    {
        public xzCompressor() : base("xz")
        {
        }

        public override Stream GetCompressor(Stream streamToWriteTo)
        {
            //Joveler.Compression.XZ
            /*
             
            var compressOptions = new XZCompressOptions()
            {
                LeaveOpen = true
            };

            var threadedCompressOptions = new XZThreadedCompressOptions()
            {
                Threads = Environment.ProcessorCount
            };

            
            XZInit.GlobalInit();

            var verInst = XZInit.Version();
            Console.WriteLine($"liblzma Version (Version) = {verInst}");

            var verStr = XZInit.VersionString();
            Console.WriteLine($"liblzma Version (String)  = {verStr}");

            
            var result = new Joveler.Compression.XZ.XZStream(streamToWriteTo, compressOptions, threadedCompressOptions);
            return result;
            */

            var result = new XZ.NET.XZOutputStream(streamToWriteTo, Environment.ProcessorCount, 0, true);
            return result;
        }

        public override Stream GetDecompressor(Stream streamToReadFrom)
        {
            var result = new XZ.NET.XZInputStream(streamToReadFrom, true);

            return result;
        }
    }
}
