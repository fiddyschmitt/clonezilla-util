using DokanNet;
using DokanNet.Logging;
using libCommon;
using libDokan;
using libDokan.Processes;
using libDokan.VFS.Folders;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static DokanNet.Dokan;

namespace libClonezilla.VFS
{
    public class OnDemandVFS : IVFS
    {
        const int MaxMountAttempts = 3;

        public OnDemandVFS(string programName, string? mountPoint, bool allowMountPointFallback = false)
        {
            ProgramName = programName;
            MountPoint = mountPoint;    //null = auto-select a drive letter at mount time
            AllowMountPointFallback = allowMountPointFallback;

            try
            {
                using var dokan = new Dokan(new NullLogger());

                Log.Logger.Information($"Detected Dokan version {dokan.Version}, Driver version {dokan.DriverVersion}.");
            }
            catch
            {
                Log.Fatal("Dokan driver not detected. Please install the version specified here: https://github.com/fiddyschmitt/clonezilla-util/releases/latest");
                Environment.Exit(1);
            }

            //requesting the root will trigger the Virtual File System to be created
            RootFolder = new Lazy<RootFolder>(() =>
            {
                //An auto letter is chosen HERE, at mount time - not at construction time. The gap
                //between choosing a letter and mounting it is where another process can pick (or be
                //granted) the same letter; under heavy load that gap used to stretch to minutes and
                //two clonezilla-util processes collided on one letter (2026-07-17, Investigation I2
                //in PERFORMANCE_PLAN.md).
                var candidateMountPoint = MountPoint ?? libDokan.Utility.GetAvailableDriveLetter();
                var triedMountPoints = new List<string>();

                for (var attempt = 1; ; attempt++)
                {
                    var root = TryMount(candidateMountPoint, out var failureReason);
                    if (root != null)
                    {
                        MountPoint = candidateMountPoint;
                        return root;
                    }

                    Log.Warning($"Could not mount the virtual file system at {candidateMountPoint}: {failureReason}");

                    //a user-chosen mount point must not be silently substituted; an auto-chosen one can be
                    if (!AllowMountPointFallback || attempt >= MaxMountAttempts)
                    {
                        Log.Fatal($"Could not mount the virtual file system at {candidateMountPoint}. Exiting.");
                        Environment.Exit(1);
                    }

                    triedMountPoints.Add(candidateMountPoint);
                    candidateMountPoint = libDokan.Utility.GetAvailableDriveLetter(excluding: triedMountPoints);
                    Log.Information($"Trying {candidateMountPoint} instead.");
                }
            });
            TempFolderRoot = new Lazy<Folder>(() =>
            {
                var tempFolderRootName = Path.GetFileNameWithoutExtension(Path.GetFileName(TempUtility.GetTempFilename(false)));

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

        /// <summary>
        /// Mounts a fresh VFS at <paramref name="mountPoint"/> and verifies the volume that appears there
        /// is actually ours. The drive letter merely appearing is not proof: a foreign volume (e.g. rclone,
        /// subst, a network mapping - possibly from another session, invisible to the free-letter scan) can
        /// own the letter, in which case our paths "don't exist" on it and every subsequent open fails
        /// (this crashed a 4h run - see PERFORMANCE_PLAN.md Investigation I1). So the VFS plants a
        /// uniquely-named sentinel entry that only this instance can serve, and we probe for it through
        /// the mounted letter before declaring the mount good.
        /// </summary>
        RootFolder? TryMount(string mountPoint, out string failureReason)
        {
            //If a live volume currently answers at this letter, it belongs to someone - possibly
            //another clonezilla-util mid-serve. Never RemoveMountPoint it: that destroys the other
            //process's mount out from under its open handles (2026-07-17: a slow ListContents
            //process did exactly this to the suite's L: mount mid-copy - Investigation I2). Report
            //the letter as unavailable instead; the caller retries elsewhere or fails fast.
            var probeRoot = mountPoint.TrimEnd('\\') + "\\";
            if (Directory.Exists(probeRoot))
            {
                failureReason = "a live volume already answers at this mount point; refusing to displace it";
                return null;
            }

            //The letter is not answering, but a dead Dokan mount entry can still be registered there
            //(e.g. its owner died without unmounting), which makes our Mount() fail. Clearing a dead
            //entry harms nobody - this is the ONLY case where RemoveMountPoint is used. A live
            //volume can still appear between the check above and the mount below; the sentinel
            //probe at the end catches that (we'd be the squatter, and our sentinel wouldn't serve).
            try
            {
                using var dokan = new Dokan(new NullLogger());
                dokan.RemoveMountPoint(mountPoint);
            }
            catch { }

            var root = new RootFolder(mountPoint);

            //unlisted: reachable by exact path but absent from directory listings
            var sentinelName = $"clonezilla-util-sentinel-{Guid.NewGuid():N}";
            _ = new UnlistedFolder(sentinelName, root);

            var vfs = new DokanVFS(ProgramName, root);

            //Signalled to abandon a failed attempt (ends the task, which unmounts via the using).
            //Deliberately not disposed: on success the mount (and this handle) live for the process lifetime.
            var stopMount = new ManualResetEvent(false);

            Task.Factory.StartNew(() =>
            {
                try
                {
                    //vfs.Mount(root.MountPoint, DokanOptions.WriteProtection, 256, new DokanNet.Logging.NullLogger());

                    using var dokan = new Dokan(new NullLogger());
                    var dokanBuilder = new DokanInstanceBuilder(dokan)
                        .ConfigureOptions(options =>
                        {
                            //Didn't get mount as network to work
                            //FPS 22/11/2025: Got a bit closer by installing the network driver: .\dokanctl.exe /i n
                            //options.Options = DokanOptions.WriteProtection | DokanOptions.NetworkDrive | DokanOptions.MountManager;
                            //options.UNCName = @"\myfs\dokan";

                            options.Options = DokanOptions.WriteProtection;
                            options.MountPoint = mountPoint;

                            //Make the per-operation timeout explicit instead of relying on the driver
                            //default (15s). After the Dokan layer fixes, callbacks are fast and
                            //non-blocking, so this is just a safety net before Dokan abandons an IRP
                            //(which surfaces to the app as 0x800705AA); 20s is generous for one read.
                            options.TimeOut = TimeSpan.FromSeconds(20);

                            //Leave SingleThread at its default (false): Dokan dispatches callbacks on
                            //multiple threads, so concurrent reads/lookups are not serialised.
                        });
                    using var dokanInstance = dokanBuilder.Build(vfs);

                    stopMount.WaitOne();
                }
                catch (Exception ex)
                {
                    Log.Error(ex.ToString());
                }
            }, TaskCreationOptions.LongRunning);

            if (!Utility.WaitForFolderToExist(mountPoint, TimeSpan.FromSeconds(30)))
            {
                failureReason = "the mount point did not appear within 30 seconds (the Dokan mount likely failed; see any errors above)";
                stopMount.Set();
                return null;
            }

            //the letter exists - but is the volume behind it OURS? Probe the sentinel that only this
            //VFS instance contains. Briefly retried, in case our own mount is still settling.
            var mountRoot = mountPoint.TrimEnd('\\') + "\\";
            var sentinelPath = Path.Combine(mountRoot, sentinelName);
            var sentinelVisible = false;
            for (var i = 0; i < 20 && !sentinelVisible; i++)
            {
                try { sentinelVisible = Directory.Exists(sentinelPath); } catch { }
                if (!sentinelVisible) Thread.Sleep(100);
            }

            if (!sentinelVisible)
            {
                var volumeDescription = "";
                try
                {
                    var drive = new DriveInfo(mountRoot);
                    volumeDescription = $" (label '{drive.VolumeLabel}', format {drive.DriveFormat})";
                }
                catch { }

                failureReason = $"it is already served by a foreign volume{volumeDescription} - e.g. rclone, subst or a network mapping, possibly from another session";
                stopMount.Set();
                return null;
            }

            failureReason = "";
            return root;
        }

        public string ProgramName
        { get; }
        public string MountPoint { get; private set; }
        public bool AllowMountPointFallback { get; }
        public Lazy<RootFolder> RootFolder { get; }
        public Lazy<Folder> TempFolderRoot { get; }

        public Folder CreateTempFolder()
        {
            var tempFolderName = Path.GetFileNameWithoutExtension(Path.GetFileName(TempUtility.GetTempFilename(false)));
            var tempFolder = new Folder(tempFolderName, TempFolderRoot.Value);

            return tempFolder;
        }
    }
}
