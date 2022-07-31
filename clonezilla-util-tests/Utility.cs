using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace clonezilla_util_tests
{
    public static class Utility
    {
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
    }
}
