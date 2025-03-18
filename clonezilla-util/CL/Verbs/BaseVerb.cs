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

        [Option("cache-folder", HelpText = "The folder to store cache files. Defaults to the 'cache' folder in the same location as the exe.", Required = false)]
        public string? CacheFolder { get; set; }

        [Option("temp-folder", HelpText = "The folder to store temporary files (if required)", Required = false)]
        public string? TempFolder { get; set; }

        [Option("process-trailing-nulls ", HelpText = "By default, the program skips trailing nulls to speed up processing of large files. Use this switch to force them to be processed.", Required = false)]
        public bool ProcessTrailingNulls { get; set; } = false;
    }
}
