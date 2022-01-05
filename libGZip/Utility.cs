using libCommon;
using Microsoft.Win32.SafeHandles;
using Serilog;
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
    }
}
