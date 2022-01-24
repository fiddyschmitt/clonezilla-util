using libCommon;
using Serilog;
using System.Runtime.InteropServices;

namespace lib7Zip
{
    public class SevenZipUtility
    {
        public static string SevenZipExe()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Environment.Is64BitOperatingSystem) return @"ext\7-Zip\win-x64\7z.exe";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && !Environment.Is64BitOperatingSystem) return @"ext\7-Zip\win-x86\7z.exe";

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return @"ext/7-Zip/linux-x64/7zz";

            throw new Exception("OS not supported yet.");
        }

        public static string SevenZipDll()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Environment.Is64BitOperatingSystem) return @"ext\7-Zip\win-x64\7z.dll";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && !Environment.Is64BitOperatingSystem) return @"ext\7-Zip\win-x86\7z.dll";

            throw new Exception("OS not supported yet.");
        }

        public static void ExtractFileToFolder(string inputFilename, string outputFolder, bool verbose, bool throwExceptionIfProcessHadErrors)
        {
            var sevenZipOutput = ProcessUtility.RunCommand(SevenZipExe(), $"x \"{inputFilename}\" -p\"blah\" -r -y -o\"{outputFolder}\"", verbose, throwExceptionIfProcessHadErrors);

            //iteration will finish when the program has exited
            _ = sevenZipOutput.ToList();
        }

        public static void ExtractFileFromArchive(string archiveFilename, string fileInArchive, Stream outputStream)
        {
            var args = $"e \"{archiveFilename}\" \"{fileInArchive}\" -so";

            ProcessUtility.ExecuteProcess(SevenZipExe(), args, null, outputStream);
        }

        public static Stream ExtractFileFromArchive(string archiveFilename, string fileInArchive)
        {
            var args = $"e \"{archiveFilename}\" \"{fileInArchive}\" -so";

            var process = ProcessUtility.ExecuteProcess(SevenZipExe(), args, null);
            //var result = process.StandardOutput.BaseStream;
            var result = process.StandardOutput.BaseStream;
            return result;
        }

        public static IEnumerable<ArchiveEntry> GetArchiveEntries(string archiveFilename, bool verbose, bool throwExceptionIfProcessHadErrors, Func<bool>? shouldStop = null)
        {
            Func<string, bool>? shouldStopProcess = null;
            if (shouldStop != null)
            {
                shouldStopProcess = line =>
                {
                    var requestStop = shouldStop();
                    return requestStop;
                };
            }

            var sevenZipOutput = ProcessUtility.RunCommand(SevenZipExe(), $"l -slt \"{archiveFilename}\"", verbose, throwExceptionIfProcessHadErrors, shouldStopProcess);

            ArchiveEntry? currentEntry = null;
            foreach (var line in sevenZipOutput)
            {
                if (line.StartsWith($"Path ="))
                {
                    string name = line.Replace("Path = ", "");

                    if (name.Equals(archiveFilename)) continue;
                    currentEntry = new ArchiveEntry(name);
                }

                if (string.IsNullOrEmpty(line))
                {
                    if (currentEntry != null)
                    {
                        yield return currentEntry;
                        currentEntry = null;
                    }

                    continue;
                }

                if (currentEntry == null) continue;

                if (line.Equals($"Folder = +")) currentEntry.IsFolder = true;

                if (!currentEntry.IsFolder)
                {
                    if (line.StartsWith($"Size =")) currentEntry.Size = long.Parse(line.Replace("Size = ", ""));
                    if (line.StartsWith($"Offset =")) currentEntry.Offset = long.Parse(line.Replace("Offset = ", ""));
                }

                if (line.StartsWith($"Modified =")) DateTime.TryParse(line.Replace("Modified = ", ""), out currentEntry.Modified);
                if (line.StartsWith($"Created =")) DateTime.TryParse(line.Replace("Created = ", ""), out currentEntry.Created);
                if (line.StartsWith($"Accessed =")) DateTime.TryParse(line.Replace("Accessed = ", ""), out currentEntry.Accessed);
            }
        }

        public static IEnumerable<string> GetArchivesInFolder(string inputFolder, bool verbose, bool throwExceptionIfProcessHadErrors)
        {
            var sevenZipOutput = ProcessUtility.RunCommand(SevenZipExe(), $"l \"{inputFolder.EnsureEndsInPathSeparator()}\"", verbose, throwExceptionIfProcessHadErrors);

            foreach (var line in sevenZipOutput)
            {
                if (line.StartsWith($"Path ="))
                {
                    var archiveFilename = line.Replace("Path = ", "");
                    if (File.Exists(archiveFilename))
                    {
                        if (verbose)
                        {
                            Log.Information($"Found archive: {archiveFilename}");
                        }
                        yield return archiveFilename;
                    }
                }
            }
        }

        public static bool IsArchive(string filename)
        {
            var sevenZipOutput = ProcessUtility.RunCommand(SevenZipExe(), $"l \"{filename}\"", false, true);

            foreach (var line in sevenZipOutput)
            {
                if (line.StartsWith($"Path ="))
                {
                    return true;
                }
            }

            return false;
        }
    }
}