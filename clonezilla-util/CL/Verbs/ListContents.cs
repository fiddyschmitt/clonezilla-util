using CommandLine;
using System;
using System.Collections.Generic;
using System.Text;

namespace clonezilla_util.CL.Verbs
{
    [Verb("list", HelpText = "List files in the archive")]
    public class ListContents : BaseVerb
    {
        [Option('s', "separator", HelpText = "The seperator to use between results in the output", Default = "\n", Required = false)]
        public string OutputSeparator { get; set; } = Environment.NewLine;

        [Option("null-separator", HelpText = "Use a null character to seperate the results in the output", Default = false)]
        public bool UseNullSeparator { get; set; } = false;

        [Option('p', "partitions", HelpText = "The partition(s) to list contents of. Eg. sda1. If not provided, all partitions will be processed.", Required = false)]
        public IEnumerable<string> PartitionsToInspect { get; set; } = [];
    }
}
