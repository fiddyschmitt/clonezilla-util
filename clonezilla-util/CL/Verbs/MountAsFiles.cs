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
        public IEnumerable<string> PartitionsToMount { get; set; } = [];

        //bool? rather than bool: CommandLineParser treats plain bool options as presence-only
        //switches, and this option must accept an explicit value (--explorer false)
        [Option("explorer", Default = true, HelpText = "Whether to open an Explorer window at the mount point once mounting completes. On by default; automation (e.g. the test suite) passes --explorer false to avoid opening a window per mount.", Required = false)]
        public bool? Explorer { get; set; }
    }
}
