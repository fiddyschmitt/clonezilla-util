using libCommon;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace libGZip
{
    public class Utility
    {
        public static string GetProgramOutput(string exe, string args)
        {
            var process = RunProgram(exe, args);
            string output = process.StandardOutput.ReadToEnd();

            process.WaitForExit();

            return output;
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

        public static int ExecuteProcess(string exe, string args, Stream inputStream) => ExecuteProcess(exe, args, inputStream, null, 0);

        public static int ExecuteProcess(string exe, string args, Stream? inputStream, Stream? outputStream, long bytesToRead)
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

            Debug.WriteLine($"{exe} {args}");
            process.Start();

            Task? sendInputTask = null;
            if (inputStream != null)
            {
                sendInputTask = Task.Factory.StartNew(() =>
                {
                    try
                    {
                        inputStream.CopyToEnd(process.StandardInput.BaseStream, Buffers.SUPER_ARBITARY_LARGE_SIZE_BUFFER);
                    }
                    catch { }

                    process.StandardInput.BaseStream.Close();
                });
            }

            int bytesRead = 0;
            if (outputStream != null)
            {
                bytesRead = (int)process.StandardOutput.BaseStream.CopyTo(outputStream, bytesToRead, Buffers.SUPER_ARBITARY_LARGE_SIZE_BUFFER);
                process.StandardOutput.Close();
                process.WaitForExit();
            }

            sendInputTask?.Wait();

            if (outputStream == null)
            {
                return 0;
            }
            else
            {
                var result = bytesRead;

                if (bytesRead != bytesToRead)
                {
                    Console.WriteLine("Problem");
                }

                var duration = DateTime.Now - start;
                Debug.WriteLine($"Read {result:N0} bytes in {duration.TotalSeconds:N2} seconds");
                Debug.WriteLine("");

                return result;
            }
        }
    }
}
