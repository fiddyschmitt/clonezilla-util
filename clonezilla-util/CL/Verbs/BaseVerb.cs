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

        //Ignored: nothing writes temporary files (decoded data stays in memory). Still accepted, hidden, so existing
        //scripts that pass it keep working; using it logs a warning (gh review F49).
        [Option("temp-folder", Hidden = true, HelpText = "Ignored. clonezilla-util writes no temporary files.", Required = false)]
        public string? TempFolder { get; set; }

        [Option("process-trailing-nulls", HelpText = "By default, the program skips trailing nulls to speed up processing of large files. Use this switch to force them to be processed.", Required = false)]
        public bool ProcessTrailingNulls { get; set; } = false;

        [Option("verbose", HelpText = "Enable verbose (Debug-level) logging. Off by default: the per-operation debug trace is suppressed because emitting it (OutputDebugString, under a global lock) costs a large share of thread-time while a mount is under load.", Required = false)]
        public bool Verbose { get; set; } = false;

        [Option("memory-reserve", HelpText = "Physical RAM (in MB) to always leave free for the OS and other programs. The decoded-span cache grows to fill the rest and shrinks when free memory drops below this. Default: max(1 GB, 8% of RAM). Env: CLONEZILLA_MEMORY_RESERVE_MB.", Required = false)]
        public int? MemoryReserveMb { get; set; }

        [Option("cache-max", HelpText = "Optional hard cap (in MB) on the total decoded-span cache, regardless of free memory. Unlimited by default. Env: CLONEZILLA_CACHE_MAX_MB.", Required = false)]
        public int? CacheMaxMb { get; set; }
    }
}
