using libUIHelpers;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace libCommon
{
    public class ProcessUtility
    {
        public static IEnumerable<string> RunCommand(string cpath, string args, bool verbose, bool throwExceptionIfProcessHadErrors, Func<string, bool>? shouldStop = null, CancellationTokenSource? cancellationTokenSource = null)
        {
            using var p = new Process();
            p.StartInfo.FileName = cpath;
            p.StartInfo.Arguments = args;

            p.StartInfo.UseShellExecute = false;
            p.StartInfo.RedirectStandardOutput = true;
            p.StartInfo.RedirectStandardError = true;

            var outputLines = new BlockingCollection<string>();
            var errorLines = new List<string>();

            using var stdoutWaitHandle = new AutoResetEvent(false);
            using var stderrWaitHandle = new AutoResetEvent(false);

            var outputDataReceived = new Action<string?>(data =>
            {
                // attach event handler
                if (data == null)
                {
                    if (verbose)
                    {
                        Log.Information($"No more data");
                    }

                    lock (outputLines)
                    {
                        outputLines.CompleteAdding();
                    }

                    try
                    {
                        stdoutWaitHandle.Set();
                    }
                    catch { }
                }
                else
                {
                    if (verbose)
                    {
                        Log.Information($"{data}");
                    }

                    bool stopRequested = shouldStop?.Invoke(data) ?? false;

                    if (stopRequested)
                    {
                        p.Close();
                    }

                    if (!outputLines.IsAddingCompleted)
                    {
                        outputLines.Add(data);
                    }
                }
            });
            p.OutputDataReceived += (sender, e) => outputDataReceived(e.Data);

            var errorDataReceived = new Action<string?>(data =>
            {
                if (data == null)
                {
                    try
                    {
                        stderrWaitHandle.Set();
                    }
                    catch { }
                }
                else
                {
                    errorLines.Add(data);

                    if (verbose)
                    {
                        Log.Information($"{data}");
                    }

                    lock (outputLines)
                    {
                        if (!outputLines.IsAddingCompleted)
                        {
                            outputLines.Add(data);
                        }
                    }
                }
            });
            p.ErrorDataReceived += (sender, e) => errorDataReceived(e.Data);

            p.Start();

            //The terminator
            var processTerminatorCancellationToken = new CancellationTokenSource();
            var processTerminator = Task.Factory.StartNew(() =>
            {
                if (cancellationTokenSource == null) return;

                while (!processTerminatorCancellationToken.IsCancellationRequested)
                {
                    if (cancellationTokenSource.IsCancellationRequested)
                    {
                        p.Kill();
                        break;
                    }
                    Thread.Sleep(100);
                }
            });

            // begin async read
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            foreach (var line in outputLines.GetConsumingEnumerable())
            {
                yield return line;
            }

            if (verbose)
            {
                Log.Information($"Waiting for exit");
            }

            // wait for process to terminate
            var processWasCancelled = cancellationTokenSource?.IsCancellationRequested ?? false;
            if (!processWasCancelled)
            {
                p.WaitForExit();
            }
            processTerminatorCancellationToken.Cancel();

            if (verbose)
            {
                Log.Information($"Exitted. Waiting on outputWaitHandle.");
            }

            // wait on handle
            stdoutWaitHandle.WaitOne();
            stderrWaitHandle.WaitOne();

            if (throwExceptionIfProcessHadErrors && errorLines.Count > 0)
            {
                var allErrorsStr = errorLines
                                    .ToString(Environment.NewLine);

                Log.Error($"{cpath} {args}{Environment.NewLine}Process encountered errors: {allErrorsStr}");
            }

            if (verbose)
            {
                Log.Information($"Finished executing: {p.StartInfo.FileName} {p.StartInfo.Arguments}");
            }
        }

        public static string GetProgramOutput(string exe, string args)
        {
            //var process = RunProgram(exe, args);
            //string output = process.StandardOutput.ReadToEnd();   //Gets stuck on the output of clonezilla gz sda2
            //process.WaitForExit();

            var sb = new StringBuilder();

            foreach (var line in RunCommand(exe, args, false, false))
            {
                sb.AppendLine(line);
            }

            return sb.ToString();
        }

        public static Process ExecuteProcess(string exe, string args, Stream? inputStream, long? bytesToRead = null, Action<(long Read, long TotalRead)>? inputReadCallback = null)
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

            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = false;
            process.StartInfo.CreateNoWindow = true;

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

                    try
                    {
                        process.StandardInput.BaseStream.Close();
                    }
                    catch { }

                });
            }

            return process;
        }

        public static int ExecuteProcess(string exe, string args, Stream? inputStream, Stream? outputStream, long? bytesToRead = null, Action<(long Read, long TotalRead)>? inputReadCallback = null)
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

                    try
                    {
                        process.StandardInput.BaseStream.Close();
                    }
                    catch { }

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

        public static IntPtr GetMainWindowHandle(int processId)
        {
            var finder = new MainWindowFinder();
            return finder.FindMainWindow(processId);
        }

        public static IntPtr GetMainWindowHandle(int processId, IntPtr desktopHandle)
        {
            var finder = new MainWindowFinder();
            return finder.FindMainWindow(processId, desktopHandle);
        }
    }
}
