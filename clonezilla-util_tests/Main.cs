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
        public static string ExeUnderTest = @"R:\Temp\clonezilla-util release\clonezilla-util.exe";
        public static bool RunLargeTests = false;

        [AssemblyInitialize]
        public static void AssemblyInit(TestContext context)
        {
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
