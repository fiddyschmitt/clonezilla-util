using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using libCommon;
using libCommon.Streams;
using Serilog;
using libDecompression;

namespace libGZip
{
    public class GZipStreamSeekable : SeekableDecompressingStream
    {
        public List<Mapping> indexContents = [];

        public static string GZTOOL_EXE => Utility.Absolutify("ext/gztool/win-x86_64/gztool-Windows.x86_64.exe");

        //Span (uncompressed MiB) between index access points; gztool's default is 10. A cold read costs
        //on average half a span of decode-and-discard from the nearest point, so denser points directly
        //multiply scattered-read throughput (the fragmented-file pathology) at the cost of a bigger
        //on-disk index (~2.5x: e.g. 68 MB -> ~180 MB for a 45 GB partition; point windows are loaded
        //lazily, so resident memory is unaffected). Existing cached indexes keep their original density.
        const int IndexSpanMiB = 4;

        public GZipStreamSeekable(Stream compressedStream, string tempIndexFilename, string indexFilename)
        {
            CompressedStream = compressedStream;
            sharedSource = new SharedStream(compressedStream);
            IndexFilename = indexFilename;

            var indexCreationComplete = false;

            if (File.Exists(tempIndexFilename))
            {
                //resume indexing

                indexContents = GetIndexContent(compressedStream, tempIndexFilename);

                var lastIndexedCompressedStartByte = (indexContents.LastOrDefault()?.CompressedStartByte) ?? throw new Exception($"Could not determine where the previous indexing got up to. Recommend deleting the temporary cache file: {tempIndexFilename}");
                compressedStream.Seek(lastIndexedCompressedStartByte - 2, SeekOrigin.Begin);

                Log.Information($"Resuming gzip index creation.");

                ProcessUtility.ExecuteProcess(GZTOOL_EXE, $"-s {IndexSpanMiB} -n {lastIndexedCompressedStartByte - 1} -I \"{tempIndexFilename}\"", compressedStream, null, 0,
                    progress =>
                    {
                        var percentThroughCompressedSource = (double)compressedStream.Position / compressedStream.Length * 100;

                        Log.Information($"Indexed {progress.TotalRead.BytesToString()}. ({percentThroughCompressedSource:N1}% through source file)");
                    });

                indexCreationComplete = true;

                compressedStream.Seek(0, SeekOrigin.Begin);
            }
            else
            {
                if (!File.Exists(indexFilename))
                {
                    //var startTime = DateTime.Now;

                    Log.Information($"Generating gzip index.");
                    ProcessUtility.ExecuteProcess(GZTOOL_EXE, $"-s {IndexSpanMiB} -I \"{tempIndexFilename}\"", compressedStream, null, 0,
                         progress =>
                         {
                             var percentThroughCompressedSource = (double)compressedStream.Position / compressedStream.Length * 100;

                             Log.Information($"Indexed {progress.TotalRead.BytesToString()}. ({percentThroughCompressedSource:N1}% through source file)");

                             //var secs = DateTime.Now - startTime;
                             //Log.Information($"{compressedStream.Position},{compressedStream.Length},{progress.Read},{progress.TotalRead},{(ulong)secs.TotalMilliseconds}");
                         });

                    indexCreationComplete = true;
                }
            }

            if (indexCreationComplete)
            {
                Log.Information($"Finished generating gzip index. Moving to final location: {indexFilename}");
                File.Move(tempIndexFilename, indexFilename);
            }

            //Prefer serving reads in-process (parse the binary .gzi ourselves; decode with the BCL's
            //native DeflateStream via ZranResume). The old model spawned a gztool.exe subprocess for
            //EVERY read - a large per-read cost, and a reliability risk under memory pressure (a
            //failed spawn used to fail the read). gztool remains the index *builder* above, and the
            //subprocess read path remains as an automatic fallback.
            try
            {
                zranIndex = GztoolIndex.Load(indexFilename);
                indexContents = BuildMappings(zranIndex);
                Log.Debug($"gzip index parsed for in-process reads ({zranIndex.Points.Count:N0} access points).");
            }
            catch (Exception ex)
            {
                Log.Warning($"Could not parse the gzip index for in-process reads ({ex.Message}). Reads will use gztool.");
                zranIndex = null;
                indexContents = GetIndexContent(compressedStream, indexFilename);
            }
        }

        GztoolIndex? zranIndex;
        readonly SharedStream sharedSource;

        //Mapping enriched with the gztool access point it was built from (window location, bit offset)
        sealed class GzMapping : Mapping
        {
            public required GztoolIndexPoint Point;
        }

        static List<Mapping> BuildMappings(GztoolIndex index)
        {
            var result = new List<Mapping>(index.Points.Count);
            for (var i = 0; i < index.Points.Count; i++)
            {
                var point = index.Points[i];
                var next = i + 1 < index.Points.Count ? index.Points[i + 1] : null;
                result.Add(new GzMapping
                {
                    Point = point,
                    CompressedStartByte = point.CompressedOffset,
                    CompressedEndByte = next?.CompressedOffset ?? 0,
                    UncompressedStartByte = point.UncompressedOffset,
                    UncompressedEndByte = next?.UncompressedOffset ?? index.UncompressedTotalLength,
                });
            }
            return result;
        }

        public Stream CompressedStream { get; }
        public string IndexFilename { get; }

        public override List<Mapping> Blocks => indexContents;

        public override long UncompressedTotalLength => Blocks.Last().UncompressedEndByte;

        public override int ReadFromChunk(Mapping chunk, byte[] buffer, int offset, int count)
        {
            var bytesLeftInFile = Length - Position;
            count = (int)Math.Min(bytesLeftInFile, count);
            if (count <= 0) return 0;

            if (zranIndex != null && chunk is GzMapping gzChunk)
            {
                var bytesRead = ReadFromChunkInProcess(gzChunk, buffer, offset, count);
                if (bytesRead > 0) return bytesRead;

                //The in-process decode produced nothing - e.g. a multi-member gzip whose member ends
                //inside this span (gztool parses member boundaries natively; raw DEFLATE stops at the
                //member's final block). Rare; fall through to gztool for this read.
                Log.Debug($"In-process gzip read produced no data at position {Position:N0}; using gztool for this read.");
            }

            int gztoolBytesRead;
            using (var outstream = new MemoryStream(buffer, offset, count))
            {
                var instream = CompressedStream;
                instream.Seek(chunk.CompressedStartByte - 1, SeekOrigin.Begin);

                var args = $"-W -I \"{IndexFilename}\" -n {chunk.CompressedStartByte} -b {Position}";
                gztoolBytesRead = ProcessUtility.ExecuteProcess(GZTOOL_EXE, args, instream, outstream, count);
            }

            return gztoolBytesRead;
        }

        int ReadFromChunkInProcess(GzMapping chunk, byte[] buffer, int offset, int count)
        {
            var positionInChunk = Position - chunk.UncompressedStartByte;

            //own view of the shared compressed stream (position-independent, access serialised)
            var source = sharedSource.CreateView();
            try
            {
                return ZranInflate.DecodeAt(zranIndex!, chunk.Point, source, positionInChunk, buffer, offset, count);
            }
            catch (Exception ex)
            {
                //corrupt window/index/stream, or anything unexpected: let the caller fall back to gztool
                Log.Debug($"In-process gzip read failed at position {Position:N0} ({ex.Message}).");
                return 0;
            }
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
