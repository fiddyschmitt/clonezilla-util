using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
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
    }
}
