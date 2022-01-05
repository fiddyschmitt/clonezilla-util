using CommandLine;
using CommandLine.Text;
using System;
using System.Collections.Generic;
using System.Text;

namespace clonezilla_util.CL.Verbs
{
    //[Verb("extract", HelpText = "Extract files (without using directory names)")]
    public class ExtractFiles : BaseVerb
    {
        public IEnumerable<string>? Filenames;
    }
}
