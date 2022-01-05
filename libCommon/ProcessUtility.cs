using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace libCommon
{
    public class ProcessUtility
    {
        public static IEnumerable<string> RunCommand(string cpath, string[] args, bool verbose, Func<string, bool>? shouldTerminate = null)
        {
            using var p = new Process();
            p.StartInfo.FileName = cpath;
            p.StartInfo.Arguments = string.Join(" ", args);

            p.StartInfo.UseShellExecute = false;
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.RedirectStandardError = true;

            var outputLines = new BlockingCollection<string>();

            using var stdoutWaitHandle = new AutoResetEvent(false);
            using var stderrWaitHandle = new AutoResetEvent(false);
            p.OutputDataReceived += (sender, e) =>
            {
                // attach event handler
                if (e.Data == null)
                {
                    if (verbose)
                    {
                        Console.WriteLine($"No more data");
                    }

                    outputLines.CompleteAdding();
                    stdoutWaitHandle.Set();
                }
                else
                {
                    if (verbose)
                    {
                        Console.WriteLine($"{e.Data}");
                    }

                    bool isFatal = shouldTerminate?.Invoke(e.Data) ?? false;

                    if (isFatal)
                    {
                        p.Close();
                    }

                    outputLines.Add(e.Data);
                }
            };

            p.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data == null)
                {
                    stderrWaitHandle.Set();
                }
                else
                {
                    if (!outputLines.IsAddingCompleted)
                    {
                        outputLines.Add(e.Data);
                    }
                }
            };

            // start process
            p.Start();

            // begin async read
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            foreach (var line in outputLines.GetConsumingEnumerable())
            {
                yield return line;
            }

            if (verbose)
            {
                Console.WriteLine($"Waiting for exit");
            }

            // wait for process to terminate
            p.WaitForExit();

            if (verbose)
            {
                Console.WriteLine($"Exitted. Waiting on outputWaitHandle.");
            }

            // wait on handle
            stdoutWaitHandle.WaitOne();
            stderrWaitHandle.WaitOne();

            if (verbose)
            {
                Console.WriteLine($"Finished executing: {p.StartInfo.FileName} {p.StartInfo.Arguments}");
            }
        }

        public static int ExecuteProcess(string exe, string args, Stream? inputStream, Stream? outputStream, long? bytesToRead = null, Action<long>? inputReadCallback = null)
        {
            var start = DateTime.Now;

            var process = new Process();
            process.StartInfo.FileName = exe;
            process.StartInfo.Arguments = args;
            process.StartInfo.UseShellExecute = false;

            if (inputStream != null)
            {
                process.StartInfo.RedirectStandardInput = true;
            }

            if (outputStream != null)
            {
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = false;
                process.StartInfo.CreateNoWindow = true;
            }

            Log.Debug($"{exe} {args}");
            process.Start();

            Task? sendInputTask = null;
            if (inputStream != null)
            {
                sendInputTask = Task.Factory.StartNew(() =>
                {
                    try
                    {
                        inputStream.CopyTo(process.StandardInput.BaseStream, Buffers.ARBITARY_LARGE_SIZE_BUFFER, inputReadCallback);
                    }
                    catch { }

                    process.StandardInput.BaseStream.Close();
                });
            }

            int bytesRead = 0;
            if (outputStream != null)
            {
                if (bytesToRead == null)
                {
                    process.StandardOutput.BaseStream.CopyTo(outputStream);
                }
                else
                {
                    bytesRead = (int)process.StandardOutput.BaseStream.CopyTo(outputStream, bytesToRead.Value, Buffers.ARBITARY_LARGE_SIZE_BUFFER);
                }
                process.StandardOutput.Close();
            }

            sendInputTask?.Wait();

            process.WaitForExit();

            if (outputStream == null)
            {
                return 0;
            }
            else
            {
                var result = bytesRead;

                if (bytesToRead != null && bytesRead != bytesToRead)
                {
                    throw new Exception($"bytesRead != bytesToRead");
                }

                var duration = DateTime.Now - start;
                Log.Debug($"Read {result:N0} bytes in {duration.TotalSeconds:N2} seconds");
                Log.Debug("");

                return result;
            }
        }
    }
}
