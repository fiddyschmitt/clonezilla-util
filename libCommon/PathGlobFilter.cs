using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace libCommon
{
    /// <summary>
    /// Include/exclude glob matcher for the <c>extract</c> verb. Patterns are matched against the exact
    /// identity the <c>list</c> verb prints for a file - <c>containerName\partitionName\path\to\file</c> -
    /// so a line copied verbatim from <c>list</c> output works as a spec, and so do a partition-qualified
    /// path (<c>sda2\Windows\Logs\CBS.log</c>), a bare archive path (<c>Windows\Logs\CBS.log</c>), and a
    /// filename (<c>CBS.log</c>).
    ///
    /// Semantics: <c>*</c> matches any run of characters INCLUDING separators (so <c>*.log</c> is "any .log
    /// anywhere" and <c>Windows\*.dll</c> is "any .dll under Windows, at any depth"); <c>?</c> matches one
    /// character. A pattern is matched as a TRAILING sub-path of the identity - the compiled regex carries
    /// an optional <c>(?:.*\\)?</c> prefix that can only end on a <c>\</c> boundary, so <c>CBS.log</c>
    /// matches <c>...\CBS.log</c> but not <c>MyCBS.log</c>. Matching is case-insensitive (7z-on-Windows
    /// behaviour); extraction still opens the file by its real, enumerated path, so case never breaks the
    /// native lookup. Separators may be written with either <c>/</c> or <c>\</c>.
    /// </summary>
    public sealed class PathGlobFilter
    {
        readonly Regex[] includes;   //empty => match everything
        readonly Regex[] excludes;

        public PathGlobFilter(IEnumerable<string>? include, IEnumerable<string>? exclude)
        {
            includes = Compile(include);
            excludes = Compile(exclude);
        }

        static Regex[] Compile(IEnumerable<string>? patterns) =>
            (patterns ?? [])
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(ToRegex)
                .ToArray();

        /// <summary>True if <paramref name="id"/> (a <c>container\partition\path</c> identity) should be
        /// extracted: it matches at least one include (or there are none), and matches no exclude.</summary>
        public bool Matches(string id)
        {
            var included = includes.Length == 0 || includes.Any(r => r.IsMatch(id));
            if (!included) return false;
            return !excludes.Any(r => r.IsMatch(id));
        }

        static Regex ToRegex(string pattern)
        {
            //normalise the separator to '\' (list/archive paths use backslash) then translate the glob.
            var normalised = pattern.Trim().Replace('/', '\\');

            //Regex.Escape turns '*' -> "\*", '?' -> "\?", '\' -> "\\", '.' -> "\.". Replacing the escaped
            //wildcards (never a literal backslash, which is "\\") keeps every other character literal.
            var translated = Regex.Escape(normalised)
                                   .Replace(@"\*", ".*")
                                   .Replace(@"\?", ".");

            //optional leading path lets the pattern match a trailing sub-path of the identity, anchored at
            //a segment ('\') boundary so a bare name can't match mid-segment.
            var regexString = @"^(?:.*\\)?" + translated + "$";
            return new Regex(regexString, RegexOptions.IgnoreCase);
        }
    }
}
