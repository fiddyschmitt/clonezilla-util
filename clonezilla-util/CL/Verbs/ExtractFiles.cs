using CommandLine;
using System.Collections.Generic;

namespace clonezilla_util.CL.Verbs
{
    [Verb("extract", HelpText = "Extract selected files to a folder. Patterns match the same path 'list' prints (container\\partition\\path), so a line copied from 'list' works as-is.")]
    public class ExtractFiles : BaseVerb
    {
        [Option('o', "output", HelpText = "The folder to extract into. If omitted, a new subfolder named after the input is created in the current directory.", Required = false)]
        public string? OutputFolder { get; set; }

        [Option('p', "partitions", HelpText = "The partition(s) to extract from. Eg. sda1. If not provided, all partitions are searched.", Required = false)]
        public IEnumerable<string> PartitionsToExtract { get; set; } = [];

        [Option("include", HelpText = "Glob pattern(s) of files to extract, eg. \"Windows/Logs/*.log\" \"*.xml\". * and ? are wildcards; a pattern with no path separator matches by filename anywhere. If omitted, all files are extracted.", Required = false)]
        public IEnumerable<string> Include { get; set; } = [];

        [Option("exclude", HelpText = "Glob pattern(s) of files to skip. Applied after --include (exclude wins).", Required = false)]
        public IEnumerable<string> Exclude { get; set; } = [];

        [Option("flatten", HelpText = "Write files directly into the output folder without recreating their directory structure.", Required = false)]
        public bool Flatten { get; set; } = false;
    }
}
