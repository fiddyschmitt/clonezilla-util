using libCommon;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MountDocushare.Streams
{
    public class SiphonStream : Stream
    {
        public SiphonStream(Stream underlyingStream, Stream tempStorage)
        {
            UnderlyingStream = underlyingStream;
            TemporaryStorage = tempStorage;

            pump = Task.Factory.StartNew(() =>
            {
                var buffer = new byte[Buffers.ARBITARY_LARGE_SIZE_BUFFER];
                var totalBytesRead = 0L;

                while (true)
                {
                    var bytesRead = underlyingStream.Read(buffer, 0, buffer.Length);

                    if (bytesRead == 0) break;

                    totalBytesRead += bytesRead;

                    Log.Debug($"{nameof(SiphonStream)} pumped {totalBytesRead.BytesToString()} from original stream.");

                    lock (TemporaryStorage)
                    {
                        TemporaryStorage.Position = TemporaryStorage.Length;
                        TemporaryStorage.Write(buffer, 0, bytesRead);
                    }
                }

                totalLength = totalBytesRead;
            });
        }

        Task pump;

        public long position = 0;
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public Stream UnderlyingStream { get; }
        public Stream TemporaryStorage { get; }
        public bool ForceFullRead { get; }

        long? totalLength = null;
        public override long Length
        {
            get
            {
                if (totalLength == null)
                {
                    pump.Wait();
                }

                if (totalLength == null) throw new Exception($"Read finished yet {nameof(totalLength)} was null.");

                return totalLength.Value;
            }
        }

        public override long Position
        {
            get => position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override void Close()
        {
            UnderlyingStream.Close();
            TemporaryStorage.Close();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var end = Position + count;
            var bytesAvailable = WaitForPositionToBeAvailable(end);

            if (bytesAvailable == 0)
            {
                return 0;
            }

            lock (TemporaryStorage)
            {
                TemporaryStorage.Position = position;

                var bytesToRead = Math.Min(bytesAvailable, count);
                var bytesRead = TemporaryStorage.Read(buffer, offset, (int)bytesToRead);
                position += bytesRead;

                return bytesRead;
            }
        }

        long WaitForPositionToBeAvailable(long desiredPosition)
        {
            long bytesAvailable;
            while (true)
            {
                if (desiredPosition <= TemporaryStorage.Length)
                {
                    //the desired bytes is now available
                    bytesAvailable = TemporaryStorage.Length - position;
                    break;
                }

                if (desiredPosition > totalLength)
                {
                    //the total length is now known, and the desired byte is beyond it
                    bytesAvailable = TemporaryStorage.Length - position;
                    break;
                }

                Thread.Sleep(10);
            }

            bytesAvailable = Math.Max(bytesAvailable, 0);

            return bytesAvailable;
        }

        public override void Flush()
        {
            throw new NotImplementedException();
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

            //WaitForPositionToBeAvailable(position);            

            if (position > TemporaryStorage.Length)
            {
                Log.Debug($"Was asked to seek to {position.BytesToString()}, which is beyond what we have already {TemporaryStorage.Length.BytesToString()}. Just offering that instead.");

                //Experiment - see if we can avoid seeking further than what we have.
                //Surprisingly, this works fine. Likely because of Dokan handles it gracefully
                position = Math.Min(TemporaryStorage.Length, position);
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
}
