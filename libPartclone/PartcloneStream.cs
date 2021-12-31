using libCommon;
using libCommon.Streams;
using libPartclone.Cache;
using libPartclone.Metadata;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libPartclone
{
    public class PartcloneStream : Stream, ISparseAwareReader
    {
        public PartcloneImageInfo PartcloneImageInfo { get; }
        long position = 0;

        public PartcloneStream(string clonezillaArchiveName, string partitionName, Stream inputStream)
        {
            PartcloneImageInfo = new PartcloneImageInfo(clonezillaArchiveName, partitionName, inputStream);
            ClonezillaArchiveName = clonezillaArchiveName;
            PartitionName = partitionName;
        }

        public Stream Stream => this;
        public bool LatestReadWasAllNull { get; set; }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length
        {
            get
            {
                long deviceSizeBytes = (long)(PartcloneImageInfo.ImageDescV1?.FileSystemInfoV1?.DeviceSizeBytes ?? PartcloneImageInfo?.ImageDescV2?.FileSystemInfoV2?.DeviceSizeBytes ?? 0);
                return deviceSizeBytes;
            }
        }

        public override long Position
        {
            get => position;
            set => Seek(value, SeekOrigin.Begin);
        }
        public string ClonezillaArchiveName { get; }
        public string PartitionName { get; }

        public override void Flush()
        {

        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (PartcloneImageInfo == null) return 0;
            if (PartcloneImageInfo.PartcloneContentMapping == null) return 0;
            if (PartcloneImageInfo.ReadStream == null) return 0;

            var startTime = DateTime.Now;

            var pos = Position;
            var bufferPos = offset;
            var end = Position + count;

            LatestReadWasAllNull = true;

            while (true)
            {
                var bytesRead = bufferPos - offset;
                var bytesToGo = count - bytesRead;

                var range = PartcloneImageInfo.PartcloneContentMapping.FirstOrDefault(r => pos >= r.OutputFileRange.StartByte && pos <= r.OutputFileRange.EndByte);

                if (bytesToGo == 0 || pos == Length || range == null)
                {
                    break;
                }

                var bytesLeftInThisRange = range.OutputFileRange.EndByte - pos + 1;
                var bytesLeftInFile = Length - pos;

                var bytesToRead = (int)Math.Min(bytesToGo, bytesLeftInThisRange);

                var deltaFromBeginningOfRange = pos - range.OutputFileRange.StartByte;

                int read;
                if (range.IsPopulated && range.PartcloneContentRange != null)
                {
                    PartcloneImageInfo.ReadStream.Seek(range.PartcloneContentRange.StartByte + deltaFromBeginningOfRange, SeekOrigin.Begin);
                    read = PartcloneImageInfo.ReadStream.Read(buffer, bufferPos, bytesToRead);
                    LatestReadWasAllNull = false;
                }
                else
                {
                    Array.Clear(buffer, bufferPos, bytesToRead);
                    read = bytesToRead;
                }

                bufferPos += read;
                pos += read;
            }

            position = pos;

            var totalBytesRead = bufferPos - offset;

            return totalBytesRead;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            switch (origin)
            {
                case SeekOrigin.Begin:
                    position = offset;
                    break;

                case SeekOrigin.Current:
                    position += offset;
                    break;

                case SeekOrigin.End:
                    position = Length - offset;
                    break;
            }

            return position;
        }

        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotImplementedException();
        }
    }

    public class ContiguousRange
    {
        public bool IsPopulated => PartcloneContentRange != null;
        public ByteRange? PartcloneContentRange { get; init; }
        public ByteRange OutputFileRange { get; init; }

        public ContiguousRange(ByteRange? partcloneContentRange, ByteRange outputFileRange)
        {
            PartcloneContentRange = partcloneContentRange;
            OutputFileRange = outputFileRange;
        }
    }

    public class ByteRange
    {
        public long StartByte;
        public long EndByte;

        public long Length { get => EndByte - StartByte + 1; }

        public override string ToString()
        {
            string result = $"{StartByte:N0} - {EndByte:N0} ({Length:N0} bytes. {Length.BytesToString()})";
            return result;
        }
    }
}
