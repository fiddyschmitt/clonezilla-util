using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libDokan
{
    public static class Utility
    {

        public static string GetAvailableDriveLetter(bool alphabetical = false, IEnumerable<string>? excluding = null)
        {
            var existingLetters = Directory.GetLogicalDrives();   //returns entries in the form "C:\"

            //letters that look free but must not be used - e.g. ones we already tried and found
            //squatted by a foreign volume that GetLogicalDrives doesn't see (rclone in another session)
            var excluded = new HashSet<string>(excluding ?? [], StringComparer.OrdinalIgnoreCase);

            var candidates = Enumerable.Range('A', 26);
            if (!alphabetical) candidates = candidates.Reverse();

            var availableLetter = candidates
                                    .Select(i => $"{(char)i}:\\")
                                    .FirstOrDefault(candidate =>
                                        !existingLetters.Any(existing => candidate.Equals(existing, StringComparison.OrdinalIgnoreCase))
                                        && !excluded.Contains(candidate)) ?? throw new Exception($"Could not find an available drive letter.");

            return availableLetter;
        }
    }
}
