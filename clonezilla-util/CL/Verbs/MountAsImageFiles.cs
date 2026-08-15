using CommandLine;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace clonezilla_util.CL.Verbs
{
    [Verb("mount-as-image-files", HelpText = "Serve the original partition images using a Virtual File System. Requires Dokan to be installed. https://dokan-dev.github.io/")]
    public class MountAsImageFiles : BaseVerb
    {
        [Option('m', "mount", HelpText = "The drive to mount to, where the partition images will be presented. If not provided, a drive letter will automatically be chosen.", Required = false)]
        public string? MountPoint { get; set; }

        [Option('p', "partitions", HelpText = "The partition(s) to serve. Eg. sda1. If not provided, all partitions will be served.", Required = false)]
        public IEnumerable<string> PartitionsToMount { get; set; } = [];

        [Option("no-explorer", HelpText = "Do not open an Explorer window at the mount point once mounting completes. Explorer is shown by default; automation (e.g. the test suite) passes this to avoid opening a window per mount.", Required = false)]
        public bool NoExplorer { get; set; }
    }
}
