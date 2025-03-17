using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libDokan
{
    public static class Utility
    {

        public static string GetAvailableDriveLetter(bool alphabetical = false)
        {
            var existingLetters = Directory.GetLogicalDrives();

            var candidates = Enumerable.Range('A', 26);
            if (!alphabetical) candidates = candidates.Reverse();

            var availableLetter = candidates
                                    .Select(i => $"{(char)i}")
                                    .FirstOrDefault(candidate => !existingLetters.Any(existing => candidate.Equals(existing, StringComparison.CurrentCultureIgnoreCase))) ?? throw new Exception($"Could not find an available drive letter.");

            availableLetter += @":\";

            return availableLetter;
        }
    }
}
