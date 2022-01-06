using CommandLine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace clonezilla_util.CL.Verbs
{
    [Verb("mount", HelpText = "Serve the file contents as using a Virtual File System. Requires Dokan to be installed. https://dokan-dev.github.io/")]
    public class MountAsFiles : BaseVerb
    {
        [Option('m', "mount", HelpText = "The drive to mount to, where the files will be presented. If not provided, a drive letter will automatically be chosen.", Required = false)]
        public string? MountPoint { get; set; }

        [Option('p', "partitions", HelpText = "The partition(s) to serve. Eg. sda1. If not provided, all partitions will be served.", Required = false)]
        public IEnumerable<string> PartitionsToExtract { get; set; } = new List<string>();
    }
}
