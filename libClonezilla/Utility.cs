using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;
using libClonezilla.PartitionContainers;
using libDokan.VFS.Folders;
using libDokan;
using Serilog;
using System.Threading.Tasks;
using System.Threading;
using DokanNet;
using libClonezilla.Partitions;
using libClonezilla.VFS;
using static libClonezilla.Partitions.MountedPartitionImage;

namespace libClonezilla
{
    public static class Utility
    {
        /// <summary>The container name to show for an input path: the folder's (or file's) own name.
        /// Path.GetFileName returns "" when the path ends in a separator - which cmd/PowerShell tab
        /// completion appends to a directory - so a trailing separator would otherwise produce an empty
        /// container name that breaks multi-container mounts and drops the container from list/extract
        /// output (gh review F10). Trim the trailing separator first; fall back to the trimmed path for a
        /// bare drive root.</summary>
        public static string ContainerNameFromPath(string path)
        {
            //resolved first, so a relative input is named after the folder it means: "." or ".." used to become the
            //container's name
            var trimmed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var name = Path.GetFileName(trimmed);
            return string.IsNullOrEmpty(name) ? trimmed : name;
        }

        /// <summary>Ends the run (<see cref="FatalException"/>, invalid arguments) with a helpful message when the
        /// loaded containers hold no partitions to <paramref name="action"/> (e.g. "mount", "list"), naming the valid
        /// partitions when there are any.</summary>
        public static void ThrowIfNoPartitions(List<PartitionContainer> containers, string action)
        {
            var totalPartitions = containers.Sum(container => container.Partitions.Count);
            if (totalPartitions > 0) return;

            var availablePartitionNames = containers
                                            .SelectMany(container => container.AvailablePartitionNames)
                                            .Distinct()
                                            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                                            .ToList();

            throw new FatalException(FatalReason.InvalidArguments, availablePartitionNames.Count > 0
                ? $"No partitions to {action}. Valid partitions are: {string.Join(", ", availablePartitionNames)}."
                : $"No partitions to {action}.");
        }

        public static List<MountedContainer> PopulateVFS(Lazy<IVFS> vfs, Folder containersRoot, List<PartitionContainer> containers, DesiredContent desiredContent)
        {
            ThrowIfNoPartitions(containers, "mount");

            var result = containers
                            .Select(container =>
                            {
                                Folder containerFolder;

                                if (containers.Count == 1)
                                {
                                    containerFolder = containersRoot;
                                }
                                else
                                {
                                    containerFolder = new Folder(container.ContainerName, containersRoot);
                                }

                                var mountedContainer = new MountedContainer(container, containerFolder, vfs, desiredContent);
                                return mountedContainer;
                            })
                            .ToList();

            return result;
        }

        public static bool WaitForFolderToExist(string folderPath, TimeSpan? timeout = null)
        {
            Log.Information($"Waiting for {folderPath} to be available.");
            var deadline = timeout == null ? (DateTime?)null : DateTime.UtcNow + timeout;
            while (true)
            {
                if (Directory.Exists(folderPath))
                {
                    return true;
                }
                if (deadline != null && DateTime.UtcNow > deadline)
                {
                    //without a timeout a failed mount left this spinning forever
                    return false;
                }
                Thread.Sleep(100);
            }
        }
    }
}
