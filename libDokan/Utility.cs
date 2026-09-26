using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
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

        /// <summary>
        /// True when <paramref name="folder"/> is a volume mount point whose volume no longer exists. That is what a
        /// folder-mounted Dokan volume leaves behind when its process ends without unmounting (killed, crashed):
        /// the folder "exists" but can't be opened, and stays that way. A junction to a path (a user's link) and a
        /// mount point whose volume still answers are never "dead".
        /// </summary>
        public static bool IsDeadVolumeMountPoint(string folder)
        {
            try
            {
                var info = new DirectoryInfo(folder);
                if (!info.Exists || !info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
                if (info.LinkTarget?.Contains("Volume{", StringComparison.OrdinalIgnoreCase) != true) return false;
            }
            catch
            {
                return false;
            }

            try
            {
                using var entries = Directory.EnumerateFileSystemEntries(folder).GetEnumerator();
                entries.MoveNext();
                return false;   //the volume answers: live
            }
            catch (IOException)
            {
                return true;    //"could not find a part of the path" / "a device which does not exist was specified"
            }
            catch
            {
                return false;   //e.g. access denied: not provably dead, so leave it alone
            }
        }

        /// <summary>
        /// Turns a volume mount point back into the plain folder underneath it, by deleting the folder's own reparse
        /// point (FSCTL_DELETE_REPARSE_POINT). Unlike mountvol or dokanctl this needs no elevation, only write access
        /// to the folder. Callers must only use it on a dead mount point (<see cref="IsDeadVolumeMountPoint"/>).
        /// </summary>
        public static bool TryClearVolumeMountPoint(string folder, out string error)
        {
            using var handle = CreateFileW(folder, GENERIC_WRITE, 0, IntPtr.Zero, OPEN_EXISTING,
                                           FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return false;
            }

            //a REPARSE_GUID_DATA_BUFFER header naming the tag to delete: ReparseTag, ReparseDataLength = 0, Reserved
            var header = new byte[8];
            BitConverter.GetBytes(IO_REPARSE_TAG_MOUNT_POINT).CopyTo(header, 0);
            if (!DeviceIoControl(handle, FSCTL_DELETE_REPARSE_POINT, header, header.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            {
                error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return false;
            }

            error = "";
            return true;
        }

        const uint GENERIC_WRITE = 0x40000000;
        const uint OPEN_EXISTING = 3;
        const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
        const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;
        const uint FSCTL_DELETE_REPARSE_POINT = 0x000900AC;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
                                                 uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, byte[] lpInBuffer, int nInBufferSize,
                                           IntPtr lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);
    }
}
