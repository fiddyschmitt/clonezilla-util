using DokanNet;
using libDokan;
using libDokan.Processes;
using libDokan.VFS.Folders;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libClonezilla.VFS
{
    public class OnDemandVFS : IVFS
    {
        public OnDemandVFS(string programName, string mountPoint)
        {
            ProgramName = programName;
            MountPoint = mountPoint;

            //requesting the root will trigger the Virtual File System to be created
            RootFolder = new Lazy<RootFolder>(() =>
            {
                //start the Virtual File System
                var root = new RootFolder(MountPoint);
                var vfs = new DokanVFS(ProgramName, root);
                Task.Factory.StartNew(() => vfs.Mount(root.MountPoint, DokanOptions.WriteProtection, 256, new DokanNet.Logging.NullLogger()));
                Utility.WaitForFolderToExist(root.MountPoint);

                //Didn't get this to work
                /*
                mountPoint = @"\\myfs\fs1";
                var root = new RootFolder(MountPoint);
                var vfs = new DokanVFS(ProgramName, root);
                Task.Factory.StartNew(() => vfs.Mount(mountPoint, DokanOptions.WriteProtection | DokanOptions.NetworkDrive, 64, new DokanNet.Logging.NullLogger()));
                Utility.WaitForMountPointToBeAvailable(root.MountPoint);
                */

                return root;
            });

            TempFolderRoot = new Lazy<Folder>(() =>
            {
                var tempFolderRootName = $"{Guid.NewGuid()} - Temp";

                /*
                var processesAllowedToAccessTemp = new Func<ProcInfo, bool>(procInfo =>
                {
                    var allowed = false;

                    if (procInfo.PID == Environment.ProcessId) return true;
                    if (procInfo.ExeFilename.Equals("7z.exe")) allowed = true;

                    return allowed;
                });

                var tempFolderRoot = new RestrictedFolderByPID(tempFolderRootName, RootFolder.Value, processesAllowedToAccessTemp);
                */

                var tempFolderRoot = new UnlistedFolder(tempFolderRootName, RootFolder.Value);
                //var tempFolderRoot = new Folder(tempFolderRootName, RootFolder.Value);

                return tempFolderRoot;
            });
        }

        public string ProgramName
        { get; }
        public string MountPoint { get; }
        public Lazy<RootFolder> RootFolder { get; }
        public Lazy<Folder> TempFolderRoot { get; }

        public Folder CreateTempFolder()
        {
            var tempFolderName = $"{Guid.NewGuid()}";
            var tempFolder = new Folder(tempFolderName, TempFolderRoot.Value);

            return tempFolder;
        }
    }
}
