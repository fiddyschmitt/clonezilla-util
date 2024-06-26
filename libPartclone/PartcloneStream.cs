﻿using libCommon;
using libCommon.Streams;
using libPartclone.Cache;
using libPartclone.Lists;
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
        public PartcloneImageInfo? PartcloneImageInfo { get; }
        long position = 0;
        readonly ContiguousRange LastRange;

        private readonly object streamLock = new();

        public PartcloneStream(string containerName, string partitionName, Stream inputStream, IPartcloneCache? cache)
        {
            inputStream.Seek(0, SeekOrigin.Begin);

            PartcloneImageInfo = new PartcloneImageInfo(containerName, partitionName, inputStream, cache);
            PartitionName = partitionName;

            LastRange = PartcloneImageInfo.PartcloneContentMapping.Value.Last();
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

        readonly ContiguousRangeComparer contiguousRangeComparer = new();

        public override int Read(byte[] buffer, int offset, int count)
        {
            lock (streamLock)
            {
                if (PartcloneImageInfo == null) return 0;
                if (PartcloneImageInfo.PartcloneContentMapping == null) return 0;
                if (PartcloneImageInfo.ReadStream == null) return 0;

                if (StopReadingWhenRemainderOfFileIsNull && !LastRange.IsPopulated)
                {
                    //if the rest of the file has null bytes, the caller isn't interested

                    //check if the entire requested section is contained within the last range
                    //var readTo = Position + count;
                    //var enclosingRange = PartcloneImageInfo.PartcloneContentMapping.Value.FirstOrDefault(r => Position >= r.OutputFileRange.StartByte && readTo <= r.OutputFileRange.EndByte);
                    var enclosingRange = PartcloneImageInfo.PartcloneContentMapping.Value.BinarySearch(Position, contiguousRangeComparer);

                    if (enclosingRange != null && enclosingRange == LastRange)
                    {
                        //the rest of the file is empty, and they're not interested

                        //clear the buffer one last time and call it a day
                        Array.Clear(buffer, offset, count);

                        position = Length;
                        return 0;
                    }
                }


                LatestReadWasAllNull = true;

                if (Position == Length)
                {
                    return 0;
                }

                //var range = PartcloneImageInfo.PartcloneContentMapping.Value.FirstOrDefault(r => pos >= r.OutputFileRange.StartByte && pos <= r.OutputFileRange.EndByte);
                var range = PartcloneImageInfo.PartcloneContentMapping.Value.BinarySearch(Position, contiguousRangeComparer);

                if (range == null)
                {
                    return 0;
                }

                var bytesLeftInThisRange = range.OutputFileRange.EndByte - Position + 1;

                var bytesToRead = (int)Math.Min(count, bytesLeftInThisRange);

                var deltaFromBeginningOfRange = Position - range.OutputFileRange.StartByte;

                int bytesRead;
                if (range.IsPopulated && range.PartcloneContentRange != null)
                {
                    PartcloneImageInfo.ReadStream.Seek(range.PartcloneContentRange.StartByte + deltaFromBeginningOfRange, SeekOrigin.Begin);
                    bytesRead = PartcloneImageInfo.ReadStream.Read(buffer, offset, bytesToRead);
                    LatestReadWasAllNull = false;
                }
                else
                {
                    Array.Clear(buffer, offset, bytesToRead);
                    bytesRead = bytesToRead;
                }

                Position += bytesRead;

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
                        position = Length - offset;
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

    public class ContiguousRange(ByteRange? partcloneContentRange, ByteRange outputFileRange)
    {
        public bool IsPopulated => PartcloneContentRange != null;
        public ByteRange? PartcloneContentRange { get; init; } = partcloneContentRange;
        public ByteRange OutputFileRange { get; init; } = outputFileRange;
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
