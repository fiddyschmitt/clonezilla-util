﻿using CommandLine;
using System;
using System.Collections.Generic;
using System.Text;

namespace clonezilla_util.CL.Verbs
{
    public class BaseVerb
    {
        [Option('i', "input", HelpText = "The folder containing the Clonezilla archive. Or, a partclone filename.", Required = true)]
        public string? InputPath { get; set; }
    }
}