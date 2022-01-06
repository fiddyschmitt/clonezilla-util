using CommandLine;
using CommandLine.Text;
using System;
using System.Collections.Generic;
using System.Text;

namespace clonezilla_util.CL.Verbs
{
    [Verb("extract-partition-image", HelpText = "Extract the uncompressed, original copy of the partition.")]
    public class ExtractPartitionImage : BaseVerb
    {
        [Option('o', "output", HelpText = "The folder to extract the partition image to.", Required = true)]
        public string? OutputFolder { get; set; }

        [Option('p', "partitions", HelpText = "The partition(s) to extract from the Clonezilla archive. Eg. sda1. If not provided, all partitions will be extracted.", Required = false)]
        public IEnumerable<string> PartitionsToExtract { get; set; } = new List<string>();

        [Option("no-sparse-output", HelpText = "By default, the program produces a sparse output file which is faster to generate and takes less disk space, while being identical to the original. Using this option, a full file is produced rather than a sparse file.", Required = false)]
        public bool NoSparseOutput { get; set; } = false;
    }
}
