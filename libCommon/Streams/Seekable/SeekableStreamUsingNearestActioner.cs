using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Linq;
using System.Threading;

namespace libCommon.Streams.Seekable
{
    public class SeekableStreamUsingNearestActioner : Stream, IReadSuggestor
    {
        long position = 0;
        long? length = null;
        readonly List<Stream> Actioners = [];

        readonly Queue<(Stream Stream, long MarchDistance)> MarchesToPerform = new();
        Task? marchTask;

        //FPS 04/01/2021: This was an attempt to make a generic wrapper for Streams that don't seek. The idea is to have many streams "ready to go" at all parts of the file, and use the closest one to make a read.

        public SeekableStreamUsingNearestActioner(Func<Stream> streamFactory, long totalLength, long distanceBetweenStationsInBytes)
        {
            StreamFactory = streamFactory;
            TotalLength = totalLength;
            DistanceBetweenStationsInBytes = distanceBetweenStationsInBytes;

            var stationCount = (int)(totalLength / distanceBetweenStationsInBytes) + 1;

            CreateActioners(stationCount);

            Log.Information($"All {Actioners.Count:N0} actioners are now on station.");
        }

        void CreateActioners(int stationCount)
        {
            Log.Information($"Waiting for {stationCount:N0} actioners to get to their stations.");

            Enumerable
                .Range(0, stationCount)
                .Chunk(Environment.ProcessorCount)
                .ForEach(batch =>
                {
                    //reading in parallel is fine (even desirable) because the OS will cache the data being read from the disk. So we should end up with the streams being in lock-step until they reach their station

                    batch
                        .AsParallel()
                        .WithDegreeOfParallelism(Environment.ProcessorCount)
                        .ForAll(stationNumber =>
                        {
                            var stationStartPosition = stationNumber * DistanceBetweenStationsInBytes;

                            var stream = StreamFactory();
                            stream.CopyTo(Null, stationStartPosition, Buffers.ARBITARY_LARGE_SIZE_BUFFER);
                            Log.Information($"Actioner is on station {stationStartPosition.BytesToString()}");

                            Actioners.Add(stream);
                        });

                });
        }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length
        {
            get
            {
                if (!length.HasValue)
                {
                    var originalPosition = Position;

                    Extensions.CopyTo(this, Null, Buffers.ARBITARY_LARGE_SIZE_BUFFER);

                    length = Position;

                    //Go back to the original position
                    Seek(originalPosition, SeekOrigin.Begin);
                }

                return length.Value;
            }
        }

        public override long Position
        {
            get => position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public Func<Stream> StreamFactory { get; }
        public long TotalLength { get; }
        public long DistanceBetweenStationsInBytes { get; }

        public override void Flush()
        {
            throw new NotImplementedException();
        }

        void StartMarchingTask()
        {

            marchTask = Task.Factory.StartNew(() =>
            {
                var totalMarches = MarchesToPerform.Count;

                int marched = 0;

                while (MarchesToPerform.Count > 0)
                {
                    if (streamClosing) return;
                    if (readRequested) break;

                    (var stream, var marchDistance) = MarchesToPerform.Dequeue();

                    stream.CopyTo(Null, marchDistance, Buffers.ARBITARY_LARGE_SIZE_BUFFER);

                    if (stream.Position == TotalLength)
                    {
                        Log.Information($"Actioner has reached the end of the stream. Creating it anew.");
                        CreateActioners(1);
                    }

                    marched++;
                }

                Log.Information($"Marched {marched:N0} of {totalMarches:N0}");
            });
        }

        bool readRequested = false;
        public override int Read(byte[] buffer, int offset, int count)
        {
            readRequested = true;
            marchTask?.Wait();

            var nearestActioner = Actioners.LastOrDefault(actioner => actioner.Position <= Position);

            bool isTemporary = false;
            if (nearestActioner == null)
            {
                Log.Information($"Created temporary actioner to serve read position: {Position.BytesToString()}");
                nearestActioner = StreamFactory();
                isTemporary = true;
            }

            var toSeek = Position - nearestActioner.Position;
            nearestActioner.CopyTo(Null, toSeek, Buffers.ARBITARY_LARGE_SIZE_BUFFER);

            var bytesActuallyRead = nearestActioner.Read(buffer, offset, count);

            position += bytesActuallyRead;

            readRequested = false;

            if (!isTemporary && bytesActuallyRead > 0)
            {
                //tell all the other actioners to march this distance we just travelled
                Actioners
                    .Where(actioner => actioner != nearestActioner)
                    .Select(actioner => (actioner, bytesActuallyRead))
                    .ForEach(march =>
                    {
                        MarchesToPerform.Enqueue(march);
                    });

                if (MarchesToPerform.Count > 0)
                {
                    Log.Information($"All other actioners need to march forward {bytesActuallyRead.BytesToString()}");
                    StartMarchingTask();
                }
            }

            return bytesActuallyRead;
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

            return Position;
        }

        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotImplementedException();
        }

        public (long Start, long End) GetRecommendation(long start)
        {
            var end = Math.Min(Length, start + DistanceBetweenStationsInBytes);

            return (start, end);
        }

        bool streamClosing = false;

        public override void Close()
        {
            streamClosing = true;
            marchTask?.Wait();

            base.Close();
        }
    }
}