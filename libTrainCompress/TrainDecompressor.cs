﻿using libCommon;
using libCommon.Streams;
using libTrainCompress.Compressors;
using libTrainCompress.Lists;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libTrainCompress
{
    public class TrainDecompressor : Stream, IReadSuggestor
    {
        readonly object readLock = new();

        public TrainDecompressor(Stream compressedStream, IList<Compressor> decompressors)
        {
            CompressedStream = compressedStream;

            var binaryReader = new BinaryReader(compressedStream);

            var expectedMagicBytes = new byte[] { 0xc0, 0xff, 0xee, 0x1a, 0xbb };
            var magicBytes = binaryReader.ReadBytes(expectedMagicBytes.Length);

            if (!magicBytes.SequenceEqual(expectedMagicBytes))
            {
                throw new Exception("Not a valid Train");
            }

            var fileFormatVersion = binaryReader.ReadInt32();
            if (fileFormatVersion == 1)
            {
                if (CompressedStream.CanSeek)
                {
                    Carriages = [];

                    //for (UInt64 i = 0; i < carriages; i++)
                    ulong i = 0;
                    while (CompressedStream.Position < CompressedStream.Length)
                    {
                        var compressionFormat = binaryReader.ReadString();
                        var uncompressedStartByte = binaryReader.ReadInt64();
                        var uncompressedEndByte = binaryReader.ReadInt64();
                        var compressedLength = binaryReader.ReadInt64();

                        var independentStream = new IndependentStream(CompressedStream, readLock);
                        var carriageCompressedStream = new SubStream(independentStream, CompressedStream.Position, CompressedStream.Position + compressedLength);

                        var carriage = new Carriage(
                            carriageId: i++,
                            compressionFormat,
                            uncompressedStartByte,
                            uncompressedEndByte,
                            carriageCompressedStream,
                            decompressors
                        );
                        Carriages.Add(carriage);

                        CompressedStream.Seek(compressedLength, SeekOrigin.Current);
                    }

                    length = Carriages.Last().UncompressedEndByte;
                }
                else
                {
                    throw new Exception("Sequential streams are not yet supported");
                }
            }
            else
            {
                throw new Exception($"Unknown file format version: {fileFormatVersion}");
            }
        }

        //used when the source stream is seekable
        readonly List<Carriage>? Carriages;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        readonly long? length;
        public override long Length
        {
            get
            {
                if (length == null)
                {
                    throw new Exception("Length not known");
                }

                return length.Value;
            }
        }

        long position;
        public override long Position
        {
            get => position;
            set => Seek(value, SeekOrigin.Begin);
        }
        public Stream CompressedStream { get; }

        public override void Flush()
        {
        }

        readonly CarriageComparer carriageComparer = new();

        public override int Read(byte[] buffer, int offset, int count)
        {
            int bytesRead = 0;

            while (true)
            {
                if (CompressedStream.CanSeek)
                {
                    //var carriage = Carriages?.FirstOrDefault(s => Position >= s.UncompressedStartByte && Position < s.UncompressedEndByte);
                    var carriage = Carriages?.BinarySearch(Position, carriageComparer);

                    if (carriage == null)
                    {
                        break;
                    }

                    //determine where we should start reading in the substream
                    var positionInCarriage = Position - carriage.UncompressedStartByte;

                    var decompressor = carriage.GetDecompressionStream();

                    //read and discard anything before it
                    if (positionInCarriage > 0)
                    {
                        decompressor.CopyTo(Stream.Null, positionInCarriage, Buffers.ARBITARY_MEDIUM_SIZE_BUFFER);
                    }

                    var bytesToRead = count - bytesRead;

                    var bytesActuallyRead = decompressor.Read(buffer, offset + bytesRead, bytesToRead);
                    decompressor.Close();

                    bytesRead += bytesActuallyRead;
                    position += bytesActuallyRead;

                    if (bytesRead >= count) break;
                }
            }

            if (bytesRead > count)
            {
                throw new Exception($"Read too many bytes! Should have read {count:N0} but read {bytesRead:N0}");
            }

            return bytesRead;
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

        public (long Start, long End) GetRecommendation(long start)
        {
            if (Carriages == null) return (start, Length);

            var startIndexPoint = Carriages.BinarySearch(start, carriageComparer) ?? Carriages.First();

            return (startIndexPoint.UncompressedStartByte, startIndexPoint.UncompressedEndByte);
        }
    }

    public class Carriage(
        ulong carriageId,
        string compressionFormat,
        long uncompressedStartByte,
        long uncompressedEndByte,
        Stream compressedStream,
        IList<Compressor> decompressors
            )
    {
        public ulong CarriageId { get; } = carriageId;
        public string CompressionFormat { get; } = compressionFormat;


        public long UncompressedStartByte { get; } = uncompressedStartByte;
        public long UncompressedEndByte { get; } = uncompressedEndByte;
        public IList<Compressor> Decompressors { get; } = decompressors;

        public Stream CompressedStream { get; } = compressedStream;

        public Stream GetDecompressionStream()
        {
            var decompressor = Decompressors.First(d => d.CompressionFormat.Equals(CompressionFormat));
            CompressedStream.Seek(0, SeekOrigin.Begin);
            var result = decompressor.GetDecompressor(CompressedStream);
            return result;
        }

        public override string ToString()
        {
            var result = $"Carriage {CarriageId}, {CompressionFormat}, {CompressedStream.Length.BytesToString()} compressed, {(UncompressedEndByte - UncompressedStartByte).BytesToString()} uncompressed";
            return result;
        }
    }
}
