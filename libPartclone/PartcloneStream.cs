using libCommon;
using libCommon.Streams;
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
        public PartcloneImageInfo? PartcloneImageInfo { get; }
        long position = 0;
        readonly IPartcloneContentMap contentMap;

        private readonly object streamLock = new();

        public PartcloneStream(string containerName, string partitionName, Stream inputStream)
        {
            inputStream.Seek(0, SeekOrigin.Begin);

            PartcloneImageInfo = new PartcloneImageInfo(containerName, partitionName, inputStream);
            PartitionName = partitionName;

            contentMap = PartcloneImageInfo.ContentMap;
        }

        public Stream Stream => this;
        public bool LatestReadWasAllNull { get; set; }
        public bool StopReadingWhenRemainderOfFileIsNull { get; set; } = false;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length
        {
            get
            {
                long deviceSizeBytes = (long)(PartcloneImageInfo?.ImageDescV1?.FileSystemInfoV1?.DeviceSizeBytes ?? PartcloneImageInfo?.ImageDescV2?.FileSystemInfoV2?.DeviceSizeBytes ?? 0);
                return deviceSizeBytes;
            }
        }

        public override long Position
        {
            get => position;
            set
            {
                lock (streamLock)
                {
                    Seek(value, SeekOrigin.Begin);
                }
            }
        }
        public string PartitionName { get; }

        public override void Flush()
        {

        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            lock (streamLock)
            {
                if (PartcloneImageInfo?.ReadStream == null) return 0;

                if (StopReadingWhenRemainderOfFileIsNull && contentMap.RestIsAllNullFrom(position))
                {
                    //the rest of the file is empty, and the caller isn't interested

                    //clear the buffer one last time and call it a day
                    Array.Clear(buffer, offset, count);

                    position = Length;
                    return 0;
                }

                LatestReadWasAllNull = true;

                if (position >= Length)
                {
                    return 0;
                }

                var location = contentMap.Locate(position, count);

                if (location.Length <= 0)
                {
                    return 0;
                }

                int bytesRead;
                if (location.IsPopulated)
                {
                    PartcloneImageInfo.ReadStream.Seek(location.ContentOffset, SeekOrigin.Begin);
                    bytesRead = PartcloneImageInfo.ReadStream.Read(buffer, offset, location.Length);
                    LatestReadWasAllNull = false;
                }
                else
                {
                    Array.Clear(buffer, offset, location.Length);
                    bytesRead = location.Length;
                }

                position += bytesRead;

                return bytesRead;
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            lock (streamLock)
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
                        position = Length + offset;
                        break;
                }

                return position;
            }
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
