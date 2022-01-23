using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libDokan.Processes
{
    public class ProcInfo
    {
        public ProcInfo(int PID)
        {
            this.PID = PID;

            Proc = new Lazy<Process>(() =>
            {
                var process = Process.GetProcessById(PID);
                return process;
            });
        }

        Lazy<Process> Proc { get; }

        public int PID { get; }

        public string Name => Proc.Value.ProcessName;

        public string FullExePath
        {
            get
            {
                string result;

                try
                {
                    result = Proc.Value.MainModule?.FileName ?? "";
                }
                catch
                {
                    result = "Not accessible";
                }

                return result;
            }
        }

        public string ExeFilename => Path.GetFileName(FullExePath);
    }
}
