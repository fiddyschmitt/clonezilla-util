using libCommon;
using libPartclone;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Tasks;

namespace clonezilla_util_tests
{
    public static class Utility
    {
        public static void Log(string msg, ConsoleColor? foregroundColour = null)
        {
            Console.ResetColor();
            Console.Write($"{DateTime.Now} ");

            if (foregroundColour != null)
            {
                Console.ForegroundColor = foregroundColour.Value;
            }

            Console.Write(msg);
        }

        public static void LogLine(string msg, ConsoleColor? foregroundColour = null)
        {
            Console.ResetColor();

            if (foregroundColour != null)
            {
                Console.ForegroundColor = foregroundColour.Value;
            }

            Console.WriteLine(msg);
        }

        public static void LogResult(bool success, string commandRun, TimeSpan duration)
        {
            if (success)
            {
                Log($"Success", ConsoleColor.Green);
            }
            else
            {
                Log($"Fail", ConsoleColor.Red);
            }

            LogLine($" ({duration.TotalMinutes:N2} minutes) {commandRun}");
        }

        public static string GetProgramOutput(string exe, string args)
        {
            //var process = RunProgram(exe, args);
            //string output = process.StandardOutput.ReadToEnd();   //Gets stuck on the output of clonezilla gz sda2
            //process.WaitForExit();

            var sb = new StringBuilder();

            foreach (var line in libCommon.ProcessUtility.RunCommand(exe, args, false, false))
            {
                sb.AppendLine(line);
            }

            return sb.ToString();
        }

        public static Process RunProgram(string exe, string args)
        {
            var process = new Process();

            process.StartInfo.FileName = exe;
            process.StartInfo.Arguments = args;

            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.Start();

            return process;
        }

        [SupportedOSPlatform("windows")]
        public static void TestSeeking(Stream rawPartitionStream, FileStream outputStream)
        {
            var chunkSizes = 1 * 1024 * 1024;
            //var chunkSizes = 8000;
            var buffer = Buffers.BufferPool.Rent(chunkSizes);

            var ranges = new List<ByteRange>();
            var i = 0L;

            //var totalSize = 415797084160L;
            var totalSize = rawPartitionStream.Length;

            var r = new Random();
            while (i < totalSize)
            {
                var bytesLeft = totalSize - i;
                var len = (long)r.Next(1, chunkSizes + 1);
                len = Math.Min(len, bytesLeft - 1);

                var range = new ByteRange()
                {
                    StartByte = i,
                    EndByte = i + len
                };
                ranges.Add(range);

                i += range.Length;
            }

            outputStream.SafeFileHandle.MarkAsSparse();
            outputStream.SetLength(totalSize);

            ulong totalBytesRead = 0;
            ranges
                .OrderBy(x => Guid.NewGuid())
                .ToList()
                .ForEach(range =>
                {
                    rawPartitionStream.Seek(range.StartByte, SeekOrigin.Begin);
                    var bytesRead = rawPartitionStream.Read(buffer, 0, chunkSizes);

                    outputStream.Seek(range.StartByte, SeekOrigin.Begin);
                    outputStream.Write(buffer, 0, bytesRead);

                    totalBytesRead += (ulong)bytesRead;
                    var percentageComplete = totalBytesRead / (double)outputStream.Length * 100;
                    Serilog.Log.Information($"{totalBytesRead}    {percentageComplete:N2}%");
                });

            Buffers.BufferPool.Return(buffer);
        }

        public static void TestFullCopy(Stream partcloneStream, Stream outputStream)
        {
            var chunkSizes = 10 * 1024 * 1024;
            var buffer1 = Buffers.BufferPool.Rent(chunkSizes);
            var buffer2 = Buffers.BufferPool.Rent(chunkSizes);

            var lastReport = DateTime.MinValue;
            var totalRead = 0UL;

            using (var compareStream = File.Open(@"E:\3_raw_cz.img", FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite))
            {
                while (true)
                {
                    var bytesRead1 = partcloneStream.Read(buffer1, 0, chunkSizes);

                    var bytesRead2 = compareStream.Read(buffer2, 0, chunkSizes);

                    if (bytesRead1 != bytesRead2)
                    {
                        throw new Exception("Different read sizes");
                    }

                    if (!buffer1.IsEqualTo(buffer2))
                    {
                        throw new Exception("Not equal");
                    }



                    if (bytesRead1 == 0)
                    {
                        break;
                    }

                    totalRead += (ulong)bytesRead1;

                    if ((DateTime.Now - lastReport).TotalMilliseconds > 1000)
                    {
                        Serilog.Log.Information($"{totalRead.BytesToString()}");
                        lastReport = DateTime.Now;
                    }

                    outputStream.Write(buffer1, 0, bytesRead1);
                }
            }

            Buffers.BufferPool.Return(buffer1);
            Buffers.BufferPool.Return(buffer2);
        }
    }
}
