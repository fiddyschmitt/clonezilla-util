using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Linq;
using libCommon;
using libCommon.Streams;

namespace libGZip
{
    public class GZipStreamSeekable : Stream, IReadSegmentSuggestor
    {
        readonly long uncompressedTotalLength = 0;
        public List<Mapping> indexContents = new();

        const string GZTOOL_EXE = "ext/gztool/gztool-Windows.x86_64.exe";

        public static (long UncompressedTotalLength, List<Mapping> Mapping) GetIndexContent(Stream compressedStream, string indexFilename)
        {
            var indexInfo = Utility.GetProgramOutput(GZTOOL_EXE, $"-ll \"{indexFilename}\"");

            var lines = indexInfo
                            .Split(new char[] { '#', '\n' })
                            .ToList();

            var sizeLine = lines
                            .FirstOrDefault(line => line.Contains("Size of uncompressed file"));

            if (sizeLine == null)
            {
                throw new Exception("Could not determine size of uncompressed file.");
            }

            var uncompressedTotalLength = long.Parse(sizeLine.Split('(')[1].Split(' ')[0]);

            var indexContents = lines
                                .Select(line => line.Split(' '))
                                .Where(tokens => tokens.Length > 4)
                                .Where(tokens => tokens[1] == "@")
                                .Skip(1)    //skip the header
                                .Select(tokens => new Mapping
                                {
                                    CompressedStartByte = long.Parse(tokens[2]),
                                    UncompressedStartByte = long.Parse(tokens[4])
                                })
                                .Sandwich()
                                .Select(entry =>
                                {
                                    if (entry.Current == null) return null;

                                    if (entry.Next == null)
                                    {
                                        entry.Current.CompressedEndByte = compressedStream.Length;
                                        entry.Current.UncompressedEndByte = uncompressedTotalLength;
                                    }
                                    else
                                    {
                                        entry.Current.CompressedEndByte = entry.Next.CompressedStartByte - 1;
                                        entry.Current.UncompressedEndByte = entry.Next.UncompressedStartByte - 1;
                                    }

                                    return entry.Current;
                                })
                                .Where(entry => entry != null)
                                .Cast<Mapping>()
                                .ToList();

            return (uncompressedTotalLength, indexContents);
        }

        public GZipStreamSeekable(Stream compressedStream, string indexFilename)
        {
            CompressedStream = compressedStream;
            IndexFilename = indexFilename;

            if (File.Exists(indexFilename))
            {
                (uncompressedTotalLength, indexContents) = GetIndexContent(compressedStream, indexFilename);

                var lastIndexedCompressedByte = indexContents.LastOrDefault()?.CompressedEndByte ?? -1;

                if (lastIndexedCompressedByte == compressedStream.Length)
                {
                    //indexing is already complete
                }
                else
                {
                    var compressedByteToContinueIndexingOn = lastIndexedCompressedByte + 1;

                    compressedStream.Seek(0, SeekOrigin.Begin);
                    Console.WriteLine($"Generating gzip index: {indexFilename}");

                    Utility.ExecuteProcess(GZTOOL_EXE, $"-n {compressedByteToContinueIndexingOn} -I \"{indexFilename}\"", compressedStream, null, 0);

                    compressedStream.Seek(0, SeekOrigin.Begin);
                }
            }
            else
            {
                Utility.ExecuteProcess(GZTOOL_EXE, $"-I \"{indexFilename}\"", compressedStream, null, 0);
            }

            (uncompressedTotalLength, indexContents) = GetIndexContent(compressedStream, indexFilename);
        }

        public (long Start, long End) GetRecommendation(long start, long end)
        {
            var startIndexPoint = indexContents.Last(ent => ent.UncompressedStartByte <= start);
            var endIndexPoint = indexContents.First(ent => ent.UncompressedStartByte >= end);

            //var recommendedStart = start;
            var recommendedStart = startIndexPoint.UncompressedStartByte;   //measured to be slightly faster than just starting from the requested position
            var recommendedEnd = (endIndexPoint == null) ? indexContents.Last().UncompressedEndByte : endIndexPoint.UncompressedEndByte;

            var result = (recommendedStart, recommendedEnd);
            return result;
        }

        long position = 0;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => uncompressedTotalLength;

        public override long Position
        {
            get => position;
            set => Seek(value, SeekOrigin.Begin);
        }
        public Stream CompressedStream { get; }
        public string IndexFilename { get; }

        public override void Flush()
        {
            throw new NotImplementedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var endPos = position + count;

            var startIndexPoint = indexContents.Last(ent => ent.UncompressedStartByte <= position);
            var endIndexPoint = indexContents.First(ent => ent.UncompressedStartByte >= endPos);

            var args = $"-W -I \"{IndexFilename}\" -n {startIndexPoint.CompressedStartByte} -b {position}";

            int bytesRead = 0;
            //using (var instream = new SubStream(CompressedStream, startIndexPoint.CompressedStartByte - 1, startIndexPoint.CompressedEndByte + 1))
            using (var outstream = new MemoryStream(buffer, offset, count))
            {
                var instream = CompressedStream;
                instream.Seek(startIndexPoint.CompressedStartByte - 1, SeekOrigin.Begin);

                var bytesLeftInFile = Length - position;
                count = (int)Math.Min(bytesLeftInFile, count);
                if (count == bytesLeftInFile)
                {
                    Console.WriteLine();
                }

                bytesRead = Utility.ExecuteProcess(GZTOOL_EXE, args, instream, outstream, count);
            }

            position += bytesRead;

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
    }

    public class Mapping
    {
        public long CompressedStartByte;
        public long CompressedEndByte;
        //public long CompressedLength => CompressedEndByte - CompressedStartByte + 1;

        public long UncompressedStartByte;
        public long UncompressedEndByte;
        //public long UncompressedLength => UncompressedEndByte - UncompressedStartByte + 1;

        public override string ToString()
        {
            //string result = $"Compressed {CompressedStartByte:N0}-{CompressedEndByte:N0} == Uncompressed {UncompressedStartByte:N0}-{UncompressedEndByte:N0}";
            string result = $"Compressed {CompressedStartByte:N0} == Uncompressed {UncompressedStartByte:N0}";
            return result;
        }
    }
}
