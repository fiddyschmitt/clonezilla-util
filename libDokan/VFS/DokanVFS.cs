using DokanNet;
using libCommon;
using libDokan.Processes;
using libDokan.VFS;
using libDokan.VFS.Files;
using libDokan.VFS.Folders;
using rextractor;
using Serilog;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using static DokanNet.FormatProviders;
using static libDokan.VFS.Folders.Folder;
using FileAccess = DokanNet.FileAccess;

namespace libDokan
{
    public class DokanVFS(string volumeLabel, RootFolder root) : IDokanOperations
    {
        readonly RootFolder Root = root;
        private readonly string VolumeLabel = volumeLabel;
        private const FileAccess DataAccess = FileAccess.ReadData | FileAccess.WriteData | FileAccess.AppendData |
                                      FileAccess.Execute |
                                      FileAccess.GenericExecute | FileAccess.GenericWrite |
                                      FileAccess.GenericRead;

        private const FileAccess DataWriteAccess = FileAccess.WriteData | FileAccess.AppendData |
                                                   FileAccess.Delete |
                                                   FileAccess.GenericWrite;

        protected static string GetPath(string fileName)
        {
            return fileName;
        }

        protected static NtStatus Trace(string method, string? fileName, IDokanFileInfo? info, NtStatus result, params object?[] parameters)
        {
            var extraParameters = parameters != null && parameters.Length > 0
                ? ", " + string.Join(", ", parameters.Select(x => string.Format(DefaultFormatProvider, "{0}", x)))
                : string.Empty;

            Log.Debug(DokanFormat($"{method}('{fileName}', {info}{extraParameters}) -> {result}"));

            return result;
        }

        private static NtStatus Trace(string method, string fileName, IDokanFileInfo info,
            FileAccess access, FileShare share, FileMode mode, FileOptions options, FileAttributes attributes,
            NtStatus result)
        {
            Log.Debug(
                DokanFormat(
                    $"{method}('{fileName}', {info}, [{access}], [{share}], [{mode}], [{options}], [{attributes}]) -> {result}"));

            return result;
        }

        const long STATUS_FILE_IS_A_DIRECTORY = 0xC00000BAL;
        const FileOptions FileNonDirectoryFile = (FileOptions)0x40;

        public NtStatus CreateFile(string fileName, FileAccess access, FileShare share, FileMode mode,
            FileOptions options, FileAttributes attributes, IDokanFileInfo info)
        {
            var result = DokanResult.Success;
            var filePath = GetPath(fileName);

            if (info.IsDirectory)
            {
                try
                {
                    switch (mode)
                    {
                        case FileMode.Open:
                            if (Root.GetEntryFromPath(filePath, info.ProcessId) is not Folder)
                            {
                                return Trace(nameof(CreateFile), fileName, info, access, share, mode, options,
                                    attributes, DokanResult.PathNotFound);
                            }

                            break;

                        case FileMode.CreateNew:
                            return Trace(nameof(CreateFile), fileName, info, access, share, mode, options,
                                    attributes, DokanResult.NotImplemented);
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    return Trace(nameof(CreateFile), fileName, info, access, share, mode, options, attributes,
                        DokanResult.AccessDenied);
                }
            }
            else
            {
                var pathExists = true;
                var pathIsDirectory = false;

                var readWriteAttributes = (access & DataAccess) == 0;
                var readAccess = (access & DataWriteAccess) == 0;

                var fileSystemEntry = Root.GetEntryFromPath(filePath, info.ProcessId);
                pathExists = fileSystemEntry != null;
                pathIsDirectory = fileSystemEntry is Folder;

                //Without this block, some programs can't enumerate the drive. Eg. log4jscanner.exe
                //See: https://github.com/dokan-dev/dokan-dotnet/issues/274
                if (pathIsDirectory)
                {
                    // Explorer opens directories with GenericRead and expects success.
                    // Previously we treated GenericRead as "NonDirectoryFile", which broke folder copies
                    // (especially for special folders like Users) and caused UAC prompts / failures.
                    if ((options & FileNonDirectoryFile) != 0)
                    {
                        return Trace(nameof(CreateFile), fileName, info, access, share, mode, options, attributes,
                            (NtStatus)STATUS_FILE_IS_A_DIRECTORY);
                    }

                    // CRITICAL: Do NOT return failure here just because access is GenericRead.
                    // Explorer NEEDS GenericRead to copy folders.

                    info.IsDirectory = true;
                }

                switch (mode)
                {
                    case FileMode.Open:

                        if (pathExists)
                        {
                            // check if driver only wants to read attributes, security info, or open directory
                            if (readWriteAttributes || pathIsDirectory)
                            {
                                if (pathIsDirectory && (access & FileAccess.Delete) == FileAccess.Delete
                                    && (access & FileAccess.Synchronize) != FileAccess.Synchronize)
                                    //It is a DeleteFile request on a directory
                                    return Trace(nameof(CreateFile), fileName, info, access, share, mode, options,
                                        attributes, DokanResult.AccessDenied);

                                info.IsDirectory = pathIsDirectory;
                                // info.Context = new object();
                                // must set it to something if you return DokanError.Success

                                return Trace(nameof(CreateFile), fileName, info, access, share, mode, options,
                                    attributes, DokanResult.Success);
                            }
                        }
                        else
                        {
                            return Trace(nameof(CreateFile), fileName, info, access, share, mode, options, attributes,
                                DokanResult.FileNotFound);
                        }
                        break;

                    case FileMode.CreateNew:
                        if (pathExists)
                            return Trace(nameof(CreateFile), fileName, info, access, share, mode, options, attributes,
                                DokanResult.FileExists);
                        break;

                    case FileMode.Truncate:
                        if (!pathExists)
                            return Trace(nameof(CreateFile), fileName, info, access, share, mode, options, attributes,
                                DokanResult.FileNotFound);
                        break;
                }

                try
                {
                    if (fileSystemEntry is FileEntry file)
                    {
                        info.Context = new FileEntryStream()
                        {
                            FileEntry = file,
                            Stream = file.GetStream()
                        };
                    }
                }
                catch (UnauthorizedAccessException) // don't have access rights
                {
                    return Trace(nameof(CreateFile), fileName, info, access, share, mode, options, attributes,
                        DokanResult.AccessDenied);
                }
                catch (DirectoryNotFoundException)
                {
                    return Trace(nameof(CreateFile), fileName, info, access, share, mode, options, attributes,
                        DokanResult.PathNotFound);
                }
                /*
                catch (Exception ex)
                {
                    var hr = (uint)Marshal.GetHRForException(ex);
                    switch (hr)
                    {
                        case 0x80070020: //Sharing violation
                            return Trace(nameof(CreateFile), fileName, info, access, share, mode, options, attributes,
                                DokanResult.SharingViolation);
                        default:
                            throw;
                    }
                }
                */
            }
            return Trace(nameof(CreateFile), fileName, info, access, share, mode, options, attributes,
                result);
        }

        static void ReleaseContext(IDokanFileInfo info)
        {
            if (info.Context is FileEntryStream fileEntryStream)
            {
                //only dispose streams the handle owns; shared streams (eg. mounted partition images) live for the lifetime of the VFS
                if (fileEntryStream.FileEntry.CreatesNewStreamPerCall)
                {
                    try
                    {
                        fileEntryStream.Stream.Dispose();
                    }
                    catch { }
                }

                info.Context = null;
            }
        }

        public void Cleanup(string fileName, IDokanFileInfo info)
        {
#if TRACE
            if (info.Context != null)
                Log.Debug(DokanFormat($"{nameof(Cleanup)}('{fileName}', {info} - entering"));
#endif

            ReleaseContext(info);

            Trace(nameof(Cleanup), fileName, info, DokanResult.Success);
        }

        public void CloseFile(string fileName, IDokanFileInfo info)
        {
#if TRACE
            if (info.Context != null)
                Log.Debug(DokanFormat($"{nameof(CloseFile)}('{fileName}', {info} - entering"));
#endif

            //backstop for the occasions where Cleanup wasn't called
            ReleaseContext(info);

            Trace(nameof(CloseFile), fileName, info, DokanResult.Success);
        }

        public NtStatus ReadFile(string fileName, byte[] buffer, out int bytesRead, long offset, IDokanFileInfo info)
        {
            //Console.WriteLine($"ReadFile {buffer.Length:N0} bytes: {fileName}");

            if (info.Context == null) // memory mapped read
            {
                var fileSystemEntry = Root.GetEntryFromPath(fileName, info.ProcessId);
                if (fileSystemEntry is FileEntry file)
                {
                    lock (file.ReadLock)
                    {
                        var stream = file.GetStream();
                        try
                        {
                            stream.Position = offset;
                            bytesRead = stream.Read(buffer, 0, buffer.Length);
                        }
                        finally
                        {
                            //shared streams must stay alive for subsequent reads
                            if (file.CreatesNewStreamPerCall)
                            {
                                stream.Dispose();
                            }
                        }
                    }
                }
                else
                {
                    throw new Exception($"Could not create stream for: {fileName}");
                }
            }
            else // normal read
            {
                if (info.Context is not FileEntryStream stream) throw new Exception("info.Context stream was null");

                //lock on the FileEntry (not the per-handle wrapper), because GetStream() can return one
                //shared stream for every open handle of this file
                lock (stream.FileEntry.ReadLock)
                {
                    var distanceFromCurrentPosition = offset - stream.Stream.Position;

                    //experimental
                    //if (distanceFromCurrentPosition > Buffers.ARBITRARY_HUGE_SIZE_BUFFER * 2 && offset + buffer.Length == stream.Length)
                    //{
                    //    Log.Debug($"External process has asked to read the last {buffer.Length.BytesToString()} of the {stream.Length.BytesToString()} file. Ignoring.");

                    //    bytesRead = 0;
                    //    return DokanResult.Unsuccessful;
                    //}

                    /*
                    //7-Zip doesn't like it when we do this
                    var maxSeek = 5_000_000;
                    var toSeek = offset - stream.Position;
                    if (toSeek > maxSeek)
                    {
                        Console.WriteLine($"External process has asked to seek from {stream.Position:N0} to {offset:N0}. Ignoring.");

                        bytesRead = 0;
                        //return NtStatus.IllegalInstruction;
                        return DokanResult.Unsuccessful;
                    }
                    */

                    var toRead = Math.Min(stream.FileEntry.Length - offset, buffer.Length);
                    if (!Environment.Is64BitOperatingSystem)
                    {
                        toRead = Math.Min(toRead, Buffers.ARBITRARY_MEDIUM_SIZE_BUFFER);
                    }
                    toRead = Math.Min(toRead, Array.MaxLength);
                    toRead = Math.Max(toRead, 0);   //reads beyond EOF would otherwise produce a negative count

                    stream.Stream.Position = offset;

                    //bytesRead = stream.Read(buffer, 0, (int)toRead);
                    bytesRead = stream.Stream.ReadAtLeast(buffer, (int)toRead, false);
                }
            }
            return Trace(nameof(ReadFile), fileName, info, DokanResult.Success, "out " + bytesRead.ToString(),
                offset.ToString(CultureInfo.InvariantCulture));
        }

        public NtStatus WriteFile(string fileName, byte[] buffer, out int bytesWritten, long offset, IDokanFileInfo info)
        {
            bytesWritten = 0;

            return Trace(nameof(WriteFile), fileName, info, DokanResult.NotImplemented, "out " + bytesWritten.ToString(),
                offset.ToString(CultureInfo.InvariantCulture));
        }

        public NtStatus FlushFileBuffers(string fileName, IDokanFileInfo info)
        {
            try
            {
                if (info.Context is FileEntryStream fileEntry)
                {
                    fileEntry.Stream.Flush();
                }
                return Trace(nameof(FlushFileBuffers), fileName, info, DokanResult.Success);
            }
            catch (IOException)
            {
                return Trace(nameof(FlushFileBuffers), fileName, info, DokanResult.DiskFull);
            }
        }

        public NtStatus GetFileInformation(string fileName, out FileInformation fileInfo, IDokanFileInfo info)
        {
            //Console.WriteLine($"GetFileInformation: {fileName}");

            // may be called with info.Context == null, but usually it isn't

            var fileSystemEntry = Root.GetEntryFromPath(fileName, info.ProcessId) ?? throw new Exception($"Could not traverse to: {fileName}");
            fileInfo = fileSystemEntry.ToFileInformation();

            return Trace(nameof(GetFileInformation), fileName, info, DokanResult.Success);
        }

        public NtStatus FindFiles(string fileName, out IList<FileInformation> files, IDokanFileInfo info)
        {
            // This function is not called because FindFilesWithPattern is implemented
            // Return DokanResult.NotImplemented in FindFilesWithPattern to make FindFiles called
            files = FindFilesHelper(fileName, "*", info.ProcessId);

            return Trace(nameof(FindFiles), fileName, info, DokanResult.Success);
        }

        public NtStatus SetFileAttributes(string fileName, FileAttributes attributes, IDokanFileInfo info)
        {
            //this is a read-only virtual file system; attributes cannot be changed
            return Trace(nameof(SetFileAttributes), fileName, info, DokanResult.NotImplemented, attributes.ToString());
        }

        public NtStatus SetFileTime(string fileName, DateTime? creationTime, DateTime? lastAccessTime, DateTime? lastWriteTime, IDokanFileInfo info)
        {
            return Trace(nameof(SetFileTime), fileName, info, DokanResult.NotImplemented, creationTime, lastAccessTime, lastWriteTime);
        }

        public NtStatus DeleteFile(string fileName, IDokanFileInfo info)
        {
            //this is a read-only virtual file system
            return Trace(nameof(DeleteFile), fileName, info, DokanResult.AccessDenied);
        }

        public NtStatus DeleteDirectory(string fileName, IDokanFileInfo info)
        {
            //this is a read-only virtual file system
            return Trace(nameof(DeleteDirectory), fileName, info, DokanResult.AccessDenied);
        }

        public NtStatus MoveFile(string oldName, string newName, bool replace, IDokanFileInfo info)
        {
            return Trace(nameof(MoveFile), oldName, info, DokanResult.NotImplemented, newName,
                replace.ToString(CultureInfo.InvariantCulture));
        }

        public NtStatus SetEndOfFile(string fileName, long length, IDokanFileInfo info)
        {
            return NtStatus.NotImplemented;
        }

        public NtStatus SetAllocationSize(string fileName, long length, IDokanFileInfo info)
        {
            return NtStatus.NotImplemented;
        }

        public NtStatus LockFile(string fileName, long offset, long length, IDokanFileInfo info)
        {
            return DokanResult.NotImplemented;
        }

        public NtStatus UnlockFile(string fileName, long offset, long length, IDokanFileInfo info)
        {
            return DokanResult.NotImplemented;
        }

        long? cachedTotalInUse;
        DateTime cachedTotalInUseTime;
        readonly object diskFreeSpaceLock = new();

        public NtStatus GetDiskFreeSpace(out long freeBytesAvailable, out long totalNumberOfBytes, out long totalNumberOfFreeBytes, IDokanFileInfo info)
        {
            long totalInUse;

            //walking the whole tree is expensive and this gets called often; memoize briefly
            lock (diskFreeSpaceLock)
            {
                if (cachedTotalInUse == null || (DateTime.Now - cachedTotalInUseTime) > TimeSpan.FromSeconds(10))
                {
                    long sum = 0;
                    _ = new Folder[] { Root }
                        .Recurse(folder =>
                        {
                            var totalFileSizes = folder
                                                    .Children
                                                    .OfType<FileEntry>()
                                                    .Sum(f => f.Length);

                            sum += totalFileSizes;

                            var subfolders = folder
                                                .Children
                                                .OfType<Folder>()
                                                .ToList();

                            return subfolders;
                        })
                        .ToList();

                    cachedTotalInUse = sum;
                    cachedTotalInUseTime = DateTime.Now;
                }

                totalInUse = cachedTotalInUse.Value;
            }

            totalNumberOfBytes = totalInUse * 10;

            totalNumberOfFreeBytes = totalNumberOfBytes - totalInUse;
            freeBytesAvailable = totalNumberOfFreeBytes;

            return Trace(nameof(GetDiskFreeSpace), null, info, DokanResult.Success, "out " + freeBytesAvailable.ToString(),
                "out " + totalNumberOfBytes.ToString(), "out " + totalNumberOfFreeBytes.ToString());
        }

        public NtStatus GetVolumeInformation(out string volumeLabel, out FileSystemFeatures features,
            out string fileSystemName, out uint maximumComponentLength, IDokanFileInfo info)
        {
            volumeLabel = VolumeLabel;
            fileSystemName = "clonezilla-util";
            maximumComponentLength = 256;

            //note: no CaseSensitiveSearch, because path lookups are case-insensitive
            features = FileSystemFeatures.CasePreservedNames |
                        FileSystemFeatures.UnicodeOnDisk |
                        FileSystemFeatures.ReadOnlyVolume;

            return Trace(nameof(GetVolumeInformation), null, info, DokanResult.Success, "out " + volumeLabel,
                "out " + features.ToString(), "out " + fileSystemName);
        }

        public NtStatus GetFileSecurity(string fileName, out FileSystemSecurity? security, AccessControlSections sections,
                            IDokanFileInfo info)
        {
            security = null;
            return DokanResult.NotImplemented;
        }

        public NtStatus SetFileSecurity(string fileName, FileSystemSecurity security, AccessControlSections sections,
            IDokanFileInfo info)
        {
            return DokanResult.NotImplemented;
        }

        public NtStatus Mounted(string mountPoint, IDokanFileInfo info)
        {
            return Trace(nameof(Mounted), null, info, DokanResult.Success);
        }

        public NtStatus Unmounted(IDokanFileInfo info)
        {
            return Trace(nameof(Unmounted), null, info, DokanResult.Success);
        }

        public NtStatus FindStreams(string fileName, IntPtr enumContext, out string streamName, out long streamSize,
            IDokanFileInfo info)
        {
            streamName = string.Empty;
            streamSize = 0;
            return Trace(nameof(FindStreams), fileName, info, DokanResult.NotImplemented, enumContext.ToString(),
                "out " + streamName, "out " + streamSize.ToString());
        }

        public NtStatus FindStreams(string fileName, out IList<FileInformation> streams, IDokanFileInfo info)
        {
            streams = Array.Empty<FileInformation>();
            return Trace(nameof(FindStreams), fileName, info, DokanResult.NotImplemented);
        }

        public IList<FileInformation> FindFilesHelper(string fileName, string searchPattern, int requestingPID)
        {
            //Console.WriteLine($"FindFilesHelper: {fileName}                     {searchPattern}");

            var fileSystemEntry = Root.GetEntryFromPath(fileName, requestingPID);

            var result = new List<FileInformation>();

            if (fileSystemEntry != null && !fileSystemEntry.IsAccessibleToProcess(requestingPID))
            {
                return result;
            }

            var wildcardMatcher = new FindFilesPatternToRegex();

            if (fileSystemEntry is Folder folder)
            {
                //This was too slow for L:\partition1\Windows\WinSxS. External applications would get 'Insufficient resources' timeouts because it took longer than 20 seconds to run.
                /*
                result = folder
                            .Children
                            .Where(child => FindFilesPatternToRegex.FindFilesEmulator(searchPattern, child.Name))   //This is slow, because it has to compile the Regex object for every single file in the folder
                            .Where(child => child is not UnlistedFolder)
                            .Select(entry => entry.ToFileInformation())
                            .ToList();
                */

                IList<FileSystemEntry> matchingChildren;
                if (searchPattern.Equals("*"))
                {
                    matchingChildren = folder.Children.ToList();
                }
                else
                {
                    matchingChildren = FindFilesPatternToRegex
                                        .FindFilesEmulator(searchPattern, folder.Children, item => item.Name);   //This is much faster, because it only has to compile the Regex object once for the folder
                }

                result = matchingChildren
                            .Where(child => child is not UnlistedFolder)
                            .Select(entry => entry.ToFileInformation())
                            .ToList();
            }
            else if (fileSystemEntry is FileEntry file)
            {
                var fileInfo = file.ToFileInformation();

                result.Add(fileInfo);
            }

            return result;
        }

        public NtStatus FindFilesWithPattern(string fileName, string searchPattern, out IList<FileInformation> files,
            IDokanFileInfo info)
        {
            files = FindFilesHelper(fileName, searchPattern, info.ProcessId);

            return Trace(nameof(FindFilesWithPattern), fileName, info, DokanResult.Success);
        }

        public static void Test()
        {
            var rootFolder = new RootFolder(@"X:\");
            var subfolder1 = new Folder("2021-12-28-13-img_PB-DEVOPS1_gz", rootFolder);
            var subfolder2 = new Folder("extracted", subfolder1);

            var files = Directory
                        .GetFiles(@"E:\_img\3 - restored using clonezilla-util")
                        .Select(filename =>
                        {
                            var fi = new FileInfo(filename);

                            var file = new StreamBackedFileEntry(
                                fi.Name,
                                subfolder2,
                                () =>
                                {
                                    var stream = File.OpenRead(filename);
                                    return stream;
                                },
                                fi.Length
                                )
                            {
                                Length = fi.Length,
                                Created = fi.CreationTime,
                                Accessed = fi.LastAccessTime,
                                Modified = fi.LastWriteTime
                            };

                            return file;
                        })
                        .ToList();


            var testFS = new DokanVFS("DokanVFS", rootFolder);
            //testFS.Mount(rootFolder.MountPoint);
        }
    }

    public class FileEntryStream
    {
        public required FileEntry FileEntry;
        public required Stream Stream;
    }
}