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

namespace libCommon.Streams
{
    public class SiphonStream : Stream
    {
        public SiphonStream(Stream underlyingStream, Stream tempStorage)
        {
            UnderlyingStream = underlyingStream;
            TemporaryStorage = tempStorage;

            pump = Task.Factory.StartNew(() =>
            {
                var buffer = new byte[Buffers.ARBITRARY_LARGE_SIZE_BUFFER];
                var totalBytesRead = 0L;

                try
                {
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
                }
                catch (Exception ex)
                {
                    bool stoppedBecauseClosed;
                    lock (stateLock)
                    {
                        stoppedBecauseClosed = closing;
                    }

                    if (stoppedBecauseClosed || ex is ObjectDisposedException)
                    {
                        //expected: the consumer closed the stream before the pump had drained the source,
                        //so the underlying/temp streams were disposed out from under this read.
                        Log.Debug(ex, $"{nameof(SiphonStream)} pump stopped after {totalBytesRead.BytesToString()} because the stream was closed.");
                    }
                    else
                    {
                        Log.Error(ex, $"{nameof(SiphonStream)} pump stopped early after {totalBytesRead.BytesToString()}.");
                    }
                }
                finally
                {
                    //always record the total, otherwise readers waiting in WaitForPositionToBeAvailable() would spin forever
                    lock (stateLock)
                    {
                        totalLength = totalBytesRead;
                    }
                }
            }, TaskCreationOptions.LongRunning);
        }

        readonly Task pump;

        long position = 0;
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public Stream UnderlyingStream { get; }
        public Stream TemporaryStorage { get; }

        readonly object stateLock = new();
        long? totalLength = null;
        bool closing = false;
        public override long Length
        {
            get
            {
                pump.Wait();    //the pump always sets totalLength when it finishes (even on error)

                lock (stateLock)
                {
                    if (totalLength == null) throw new Exception($"Read finished yet {nameof(totalLength)} was null.");

                    return totalLength.Value;
                }
            }
        }

        public override long Position
        {
            get => position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override void Close()
        {
            //signal the pump (before closing the streams) that any read failure from here on is expected
            lock (stateLock)
            {
                closing = true;
            }

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
                long storedSoFar;
                lock (TemporaryStorage)
                {
                    storedSoFar = TemporaryStorage.Length;
                }

                if (desiredPosition <= storedSoFar)
                {
                    //the desired bytes is now available
                    bytesAvailable = storedSoFar - position;
                    break;
                }

                long? knownTotalLength;
                lock (stateLock)
                {
                    knownTotalLength = totalLength;
                }

                if (knownTotalLength != null && desiredPosition > knownTotalLength)
                {
                    //the total length is now known, and the desired byte is beyond it
                    bytesAvailable = storedSoFar - position;
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
                    position = Length + offset;
                    break;
            }

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
