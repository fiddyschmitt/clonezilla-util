using libCommon;
using libDecompression.Utilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libBzip2
{
    public static class BZip2BlockFinder
    {
        private static readonly byte[] StartOfBlockMagic = [0x31, 0x41, 0x59, 0x26, 0x53, 0x59];    //31 41 59 26 53 59
        //private static readonly byte[] EndOfBlockMagic = [0x17, 0x72, 0x45, 0x38, 0x50, 0x90];      //17 72 45 38 50 90       //This 'end of block' marker doesn't seem to appear in the file

        public static IEnumerable<(long Start, long End)> FindBlocks(Stream stream)
        {
            long? startOfBlock = null;
            foreach (var foundPosition in FindInstances(stream, StartOfBlockMagic))
            {
                if (startOfBlock != null)
                {
                    var endOfBlock = foundPosition;
                    //Debug.WriteLine($"{startOfBlock:N0} - {endOfBlock:N0}");
                    yield return (startOfBlock.Value, endOfBlock);
                    startOfBlock = null;
                }

                startOfBlock = foundPosition;
            }

            if (startOfBlock != null)
            {
                yield return (startOfBlock.Value, stream.Length);
            }
        }

        public static IEnumerable<long> FindInstances(this Stream stream, byte[] find)
        {
            var buff = new byte[1 * 1024 * 1024];
            var ms = new MemoryStream(buff);

            while (true)
            {
                var bufferPositionInStream = stream.Position;

                ms.Seek(0, SeekOrigin.Begin);
                var bytesRead = (int)stream.CopyTo(ms, buff.Length, buff.Length);
                if (bytesRead == 0) break;

                var positionThroughCurrentBuffer = 0;
                while (true)
                {
                    var bytesRemainingInBuffer = bytesRead - positionThroughCurrentBuffer;
                    var subsectionToSearch = new Span<byte>(buff, positionThroughCurrentBuffer, bytesRemainingInBuffer);
                    var foundPositionInBufferSubsection = BoyerMoore.IndexOf(subsectionToSearch, StartOfBlockMagic);

                    if (foundPositionInBufferSubsection == -1)
                    {
                        break;
                    }
                    else
                    {
                        var foundPositionInStream = bufferPositionInStream + positionThroughCurrentBuffer + foundPositionInBufferSubsection;
                        yield return foundPositionInStream;

                        positionThroughCurrentBuffer = positionThroughCurrentBuffer + foundPositionInBufferSubsection + 1;
                    }
                }

                //just in case the search pattern is right on the border
                if (stream.Position == stream.Length)
                {
                    break;
                }
                else
                {
                    stream.Seek(-find.Length, SeekOrigin.Current);
                }
            }
        }
    }
}
