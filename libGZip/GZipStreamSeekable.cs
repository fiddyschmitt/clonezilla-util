using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Linq;
using libCommon;
using libCommon.Streams;
using Serilog;

namespace libGZip
{
    public class GZipStreamSeekable : Stream, IReadSuggestor
    {
        readonly long uncompressedTotalLength = 0;
        public List<Mapping> indexContents = new();

        const string GZTOOL_EXE = "ext/gztool/win-x86_64/gztool-Windows.x86_64.exe";

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
                                        entry.Current.UncompressedEndByte = uncompressedTotalLength;
                                    }
                                    else
                                    {
                                        entry.Current.UncompressedEndByte = entry.Next.UncompressedStartByte - 1;
                                    }

                                    return entry.Current;
                                })
                                .Where(entry => entry != null)
                                .Cast<Mapping>()
                                .ToList();

            return (uncompressedTotalLength, indexContents);
        }

        public GZipStreamSeekable(Stream compressedStream, string tempIndexFilename, string indexFilename)
        {
            CompressedStream = compressedStream;
            IndexFilename = indexFilename;

            var indexCreationComplete = false;

            if (File.Exists(tempIndexFilename))
            {
                //resume indexing

                (uncompressedTotalLength, indexContents) = GetIndexContent(compressedStream, tempIndexFilename);

                var lastIndexedCompressedStartByte = indexContents.LastOrDefault()?.CompressedStartByte;

                if (lastIndexedCompressedStartByte == null)
                {
                    throw new Exception($"Could not determine where the previous indexing got up to. Recommend deleting the temporary cache file: {tempIndexFilename}");
                }

                compressedStream.Seek(lastIndexedCompressedStartByte.Value - 2, SeekOrigin.Begin);
                Log.Information($"Resuming gzip index creation.");

                ProcessUtility.ExecuteProcess(GZTOOL_EXE, $"-n {lastIndexedCompressedStartByte - 1} -I \"{tempIndexFilename}\"", compressedStream, null, 0,
                    totalInputRead =>
                    {
                        var totalCopiedStr = Extensions.BytesToString(compressedStream.Position);
                        var totalStr = Extensions.BytesToString(compressedStream.Length);
                        var per = (double)compressedStream.Position / compressedStream.Length * 100;

                        Log.Information($"Indexed {totalCopiedStr} / {totalStr} ({per:N0}%)");
                    });

                indexCreationComplete = true;

                compressedStream.Seek(0, SeekOrigin.Begin);
            }
            else
            {
                if (!File.Exists(indexFilename))
                {
                    Log.Information($"Generating gzip index.");
                    ProcessUtility.ExecuteProcess(GZTOOL_EXE, $"-I \"{tempIndexFilename}\"", compressedStream, null, 0,
                         totalInputRead =>
                         {
                             var totalCopiedStr = Extensions.BytesToString(compressedStream.Position);
                             var totalStr = Extensions.BytesToString(compressedStream.Length);
                             var per = (double)compressedStream.Position / compressedStream.Length * 100;

                             Log.Information($"Indexed {totalCopiedStr} / {totalStr} ({per:N0}%)");
                         });

                    indexCreationComplete = true;
                }
            }

            if (indexCreationComplete)
            {
                Log.Information($"Finished generating gzip index. Moving to final location: {indexFilename}");
                File.Move(tempIndexFilename, indexFilename);
            }

            (uncompressedTotalLength, indexContents) = GetIndexContent(compressedStream, indexFilename);
        }

        public (long Start, long End) GetRecommendation(long start, long end)
        {
            end = Math.Min(Length, end);

            var startIndexPoint = indexContents.Last(ent => start >= ent.UncompressedStartByte);
            var endIndexPoint = indexContents.First(ent => end <= ent.UncompressedEndByte);

            var recommendedStart = startIndexPoint.UncompressedStartByte;   //measured to be slightly faster than just starting from the requested position
            var recommendedEnd = endIndexPoint.UncompressedEndByte;

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

        public override void Flush() => throw new NotImplementedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var endPos = position + count;

            var startIndexPoint = indexContents.Last(ent => ent.UncompressedStartByte <= position);

            var args = $"-W -I \"{IndexFilename}\" -n {startIndexPoint.CompressedStartByte} -b {position}";

            int bytesRead = 0;
            using (var outstream = new MemoryStream(buffer, offset, count))
            {
                var instream = CompressedStream;
                instream.Seek(startIndexPoint.CompressedStartByte - 1, SeekOrigin.Begin);

                var bytesLeftInFile = Length - position;
                count = (int)Math.Min(bytesLeftInFile, count);

                bytesRead = ProcessUtility.ExecuteProcess(GZTOOL_EXE, args, instream, outstream, count);
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

        public override void SetLength(long value) => throw new NotImplementedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotImplementedException();
    }

    public class Mapping
    {
        public long CompressedStartByte;

        public long UncompressedStartByte;
        public long UncompressedEndByte;

        public override string ToString()
        {
            var uncompressedSizeStr = (UncompressedEndByte - UncompressedStartByte).BytesToString();
            string result = $"Compressed {CompressedStartByte:N0} == Uncompressed {UncompressedStartByte:N0} ({uncompressedSizeStr} uncompressed data)";
            return result;
        }
    }
}
