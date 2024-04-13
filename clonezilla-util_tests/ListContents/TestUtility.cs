using libCommon;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace clonezilla_util_tests.ListContents
{
    public static class TestUtility
    {
        public static void ConfirmContainsStrings(string exeUnderTest, string args, IList<string> expectedStrings)
        {
            var output = ProcessUtility.GetProgramOutput(exeUnderTest, args);

            expectedStrings
                .ToList()
                .ForEach(expectedString =>
                {
                    var contains = output.Contains(expectedString);
                    Assert.IsTrue(contains, $"Could not find: {expectedString}");
                });
        }
    }
}
