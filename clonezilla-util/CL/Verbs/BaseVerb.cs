using CommandLine;
using System;
using System.Collections.Generic;
using System.Text;

namespace clonezilla_util.CL.Verbs
{
    public class BaseVerb
    {
        [Option('i', "input", HelpText = "The folder containing the Clonezilla archive. Or, a partclone filename, or image filename.", Required = true, Min = 1)]
        public IEnumerable<string>? InputPaths { get; set; }

        [Option("temp-folder", HelpText = "The folder to store temporary files (if required)", Required = false)]
        public string? TempFolder { get; set; }
    }
}
