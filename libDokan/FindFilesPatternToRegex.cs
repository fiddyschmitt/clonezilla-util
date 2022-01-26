using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace rextractor
{
    public class FindFilesPatternToRegex
    {
        private static readonly Regex HasQuestionMarkRegEx = new(@"\?", RegexOptions.Compiled);
        private static readonly Regex IllegalCharactersRegex = new("[" + @"\/:<>|" + "\"]", RegexOptions.Compiled);
        private static readonly Regex CatchExtensionRegex = new(@"^\s*.+\.([^\.]+)\s*$", RegexOptions.Compiled);
        private static readonly string NonDotCharacters = @"[^.]*";
        public static Regex Convert(string pattern)
        {
            if (string.IsNullOrEmpty(pattern))
            {
                throw new ArgumentException("Pattern is empty.");
            }

            if (IllegalCharactersRegex.IsMatch(pattern))
            {
                throw new ArgumentException("Pattern contains illegal characters.");
            }

            bool hasExtension = CatchExtensionRegex.IsMatch(pattern);
            bool matchExact = false;
            if (HasQuestionMarkRegEx.IsMatch(pattern))
            {
                matchExact = true;
            }
            else if (hasExtension)
            {
                matchExact = CatchExtensionRegex.Match(pattern).Groups[1].Length != 3;
            }

            string regexString = Regex.Escape(pattern);
            regexString = "^" + Regex.Replace(regexString, @"\\\*", ".*");
            regexString = Regex.Replace(regexString, @"\\\?", ".");

            if (!matchExact && hasExtension)
            {
                regexString += NonDotCharacters;
            }

            regexString += "$";
            var regex = new Regex(regexString, RegexOptions.Compiled | RegexOptions.IgnoreCase);
            return regex;
        }

        public static IList<T> FindFilesEmulator<T>(string pattern, IEnumerable<T> items, Func<T, string> nameRetriever)
        {
            var directMatch = items
                                .Where(item =>
                                {
                                    var name = nameRetriever(item);
                                    return name.Equals(pattern, StringComparison.CurrentCultureIgnoreCase);
                                });

            if (directMatch.Any())
            {
                return directMatch.ToArray();
            }

            try
            {
                var regex = Convert(pattern);

                var matches = items
                                .Where(item =>
                                {
                                    var name = nameRetriever(item);
                                    return regex.IsMatch(name);
                                })
                                .ToList();

                return matches;
            }
            catch { }

            return new List<T>();
        }

        public static IList<string> FindFilesEmulator(string pattern, IList<string> names)
        {
            var directMatch = names.Where(name => name.Equals(pattern, StringComparison.CurrentCultureIgnoreCase));
            if (directMatch.Any())
            {
                return directMatch.ToArray();
            }

            try
            {
                var regex = Convert(pattern);

                var matches = names
                                .Where(name => regex.IsMatch(name))
                                .ToList();

                return matches;
            }
            catch { }

            return Array.Empty<string>();
        }

        //Warning, this is slow because it has to generate a Regex every time
        public static bool FindFilesEmulator(string pattern, string name)
        {
            var directMatch = name.Equals(pattern, StringComparison.CurrentCultureIgnoreCase);
            if (directMatch)
            {
                return directMatch;
            }

            try
            {
                var regex = Convert(pattern);
                if (regex.IsMatch(name))
                {
                    return true;
                }
            }
            catch { }

            return false;
        }
    }
}
