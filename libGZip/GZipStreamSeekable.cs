using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using libCommon;
using Serilog;
using libDecompression;

namespace libGZip
{
    public class GZipStreamSeekable : SeekableDecompressingStream
    {
        public List<Mapping> indexContents = [];

        public static string GZTOOL_EXE => Utility.Absolutify("ext/gztool/win-x86_64/gztool-Windows.x86_64.exe");

        public GZipStreamSeekable(Stream compressedStream, string tempIndexFilename, string indexFilename)
        {
            CompressedStream = compressedStream;
            IndexFilename = indexFilename;

            var indexCreationComplete = false;

            if (File.Exists(tempIndexFilename))
            {
                //resume indexing

                indexContents = GetIndexContent(compressedStream, tempIndexFilename);

                var lastIndexedCompressedStartByte = (indexContents.LastOrDefault()?.CompressedStartByte) ?? throw new Exception($"Could not determine where the previous indexing got up to. Recommend deleting the temporary cache file: {tempIndexFilename}");
                compressedStream.Seek(lastIndexedCompressedStartByte - 2, SeekOrigin.Begin);

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

            indexContents = GetIndexContent(compressedStream, indexFilename);
        }

        public Stream CompressedStream { get; }
        public string IndexFilename { get; }

        public override List<Mapping> Blocks => indexContents;

        public override long UncompressedTotalLength => Blocks.Last().UncompressedEndByte;

        public override int ReadFromChunk(Mapping chunk, byte[] buffer, int offset, int count)
        {
            int bytesRead = 0;
            using (var outstream = new MemoryStream(buffer, offset, count))
            {
                var instream = CompressedStream;
                instream.Seek(chunk.CompressedStartByte - 1, SeekOrigin.Begin);

                var bytesLeftInFile = Length - Position;
                count = (int)Math.Min(bytesLeftInFile, count);

                var args = $"-W -I \"{IndexFilename}\" -n {chunk.CompressedStartByte} -b {Position}";
                bytesRead = ProcessUtility.ExecuteProcess(GZTOOL_EXE, args, instream, outstream, count);
            }

            return bytesRead;
        }

        public static List<Mapping> GetIndexContent(Stream compressedStream, string indexFilename)
        {
            var indexInfo = ProcessUtility.GetProgramOutput(GZTOOL_EXE, $"-ll \"{indexFilename}\"");

            var lines = indexInfo
                            .Split(['#', '\n'])
                            .ToList();

            var sizeLine = lines
                            .FirstOrDefault(line => line.Contains("Size of uncompressed file")) ?? throw new Exception("Could not determine size of uncompressed file.");

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
                                        entry.Current.UncompressedEndByte = entry.Next.UncompressedStartByte;
                                    }

                                    return entry.Current;
                                })
                                .Where(entry => entry != null)
                                .Cast<Mapping>()
                                .ToList();

            return indexContents;
        }
    }
}
