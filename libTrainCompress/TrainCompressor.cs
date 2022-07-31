using libTrainCompress.Compressors;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libTrainCompress
{
    public class TrainCompressor : Stream
    {
        public TrainCompressor(Stream streamToWriteTo, List<Compressor> compressors, long splitSize)
        {
            WriteToStream = streamToWriteTo;
            Compressors = compressors;
            SplitSize = splitSize;
            BinaryWriter = new BinaryWriter(streamToWriteTo);
        }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        long length = 0;
        public override long Length => length;
        public override long Position { get; set; }
        public Stream WriteToStream { get; }
        public List<Compressor> Compressors { get; }
        public long SplitSize { get; }
        public BinaryWriter BinaryWriter { get; }

        public override void Flush()
        {
            if (carriageBeingFilled != null)
            {
                var uncompressedEndByte = Position;

                carriageBeingFilled.Compressor.Close();
                var compressedLength = carriageBeingFilled.CompressedLength;

                BinaryWriter.Write(carriageBeingFilled.CompressionFormat);
                BinaryWriter.Write(carriageBeingFilled.UncompressedStartByte);
                BinaryWriter.Write(uncompressedEndByte);
                BinaryWriter.Write(compressedLength);

                carriageBeingFilled.CompressedBuffer.Seek(0, SeekOrigin.Begin);
                carriageBeingFilled.CompressedBuffer.Seek(0, SeekOrigin.Begin);
                carriageBeingFilled.CompressedBuffer.CopyTo(WriteToStream);

                carriageBeingFilled = null;
            }
        }

        public override void Close()
        {
            Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }

        bool FirstWrite = true;
        CarriageBeingFilled? carriageBeingFilled;

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (FirstWrite)
            {
                var magicBytes = new byte[] { 0xc0, 0xff, 0xee, 0x1a, 0xbb };
                BinaryWriter.Write(magicBytes);

                BinaryWriter.Write(1);  //file format version

                FirstWrite = false;
            }

            var written = 0;

            while (true)
            {
                if (carriageBeingFilled == null)
                {
                    //todo: Choose between compressors based on size of output, or compression speed.
                    var compressor = Compressors.First();
                    var compressedBuffer = new MemoryStream();
                    var compressorStream = compressor.GetCompressor(compressedBuffer);

                    carriageBeingFilled = new CarriageBeingFilled(compressedBuffer, compressorStream, compressor.CompressionFormat, Position);
                }

                var spaceUsedInCarriage = Position - carriageBeingFilled.UncompressedStartByte;
                var spaceLeftInCarriage = SplitSize - spaceUsedInCarriage;
                var bytesLeftToWrite = count - written;

                if (spaceLeftInCarriage == 0)
                {
                    try
                    {
                        //some compressors (eg. XZ.net) don't support this
                        carriageBeingFilled.Compressor.Flush();
                    }
                    catch { }

                    //this carriage is full. Let's prepend a bit of metadata and send it on.
                    Flush();
                }
                else
                {
                    var toWrite = (int)Math.Min(bytesLeftToWrite, spaceLeftInCarriage);

                    if (toWrite == 0)
                    {
                        break;
                    }

                    carriageBeingFilled.Compressor.Write(buffer, offset + written, toWrite);
                    written += toWrite;

                    length += toWrite;
                    Position = length;
                }
            }
        }

        public class CarriageBeingFilled
        {
            public Stream CompressedBuffer;
            public Stream Compressor;
            public string CompressionFormat;
            public long UncompressedStartByte;

            public CarriageBeingFilled(Stream compressedBuffer, Stream compressor, string compressorFormat, long uncompressedStartByte)
            {
                CompressedBuffer = compressedBuffer;
                Compressor = compressor;
                CompressionFormat = compressorFormat;
                UncompressedStartByte = uncompressedStartByte;
            }

            public long CompressedLength => CompressedBuffer.Length;
        }
    }
}
