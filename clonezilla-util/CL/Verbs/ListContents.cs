using CommandLine;
using System;
using System.Collections.Generic;
using System.Text;

namespace clonezilla_util.CL.Verbs
{
    [Verb("l", HelpText = "List contents of archive")]
    public class ListContents// : BaseVerb  //todo: Listing contents isn't supported yet. Likely will package 7z.exe to inspect the contents
    {
        //[Value(0, HelpText = "The folder containing the Clonezilla archive.", Required = true)]
        [Option('i', "input", HelpText = "The folder containing the Clonezilla archive", Required = true)]
        public string? InputFolder { get; set; }

        [Option('s', "separator", HelpText = "The seperator to use between results in the output", Default = "\n", Required = false)]
        public string OutputSeparator { get; set; } = Environment.NewLine;

        [Option("ns", HelpText = "Use a null character to seperate the results in the output", Default = false)]
        public bool UseNullSeparator { get; set; } = false;

        [Option('p', "partitions", HelpText = "The partition(s) to extract. Eg. sda1. If not provided, all partitions will be extracted.", Required = false)]
        public IEnumerable<string> PartitionsToOpen { get; set; } = new List<string>();
    }
}
