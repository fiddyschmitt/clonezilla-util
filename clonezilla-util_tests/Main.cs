using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace clonezilla_util_tests
{
    [TestClass]
    public static class Main
    {
        public const string ExeUnderTest = @"R:\Temp\clonezilla-util release\clonezilla-util.exe";

        [AssemblyInitialize]
        public static void AssemblyInit(TestContext context)
        {
            //the exe writes its memory telemetry (the MEM lines the test analyses read) only when asked; every exe
            //the tests spawn inherits this
            Environment.SetEnvironmentVariable("CLONEZILLA_TELEMETRY", "1");

            Process
                .GetProcesses()
                .Where(pr => pr.ProcessName == "clonezilla-util")
                .ToList()
                .ForEach(p =>
                {
                    p.Kill();
                    p.WaitForExit();
                });
        }
    }
}
