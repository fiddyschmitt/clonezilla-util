using DokanNet;
using DokanNet.Logging;
using libCommon;
using libDokan;
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
            catch (Exception ex)
            {
                throw new FatalException(FatalReason.DokanNotInstalled,
                    $"Dokan driver not detected ({ex.Message}). Please install the version specified here: https://github.com/fiddyschmitt/clonezilla-util/releases/latest");
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
                        throw new FatalException(FatalReason.MountFailed, $"Could not mount the virtual file system at {candidateMountPoint}: {failureReason}");
                    }

                    triedMountPoints.Add(candidateMountPoint);
                    candidateMountPoint = libDokan.Utility.GetAvailableDriveLetter(excluding: triedMountPoints);
                    Log.Information($"Trying {candidateMountPoint} instead.");
                }
            });
            TempFolderRoot = new Lazy<Folder>(() =>
            {
                var tempFolderRootName = Path.GetFileNameWithoutExtension(Path.GetRandomFileName());
                var tempFolderRoot = new UnlistedFolder(tempFolderRootName, RootFolder.Value);

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
            //A drive letter ("L:\") or a folder mount point. The caller (Program.TryResolveMountPoint)
            //normalises letters to that form and has already checked a folder is an existing, empty NTFS
            //folder that is not a live mount point or link (gh review F51).
            var isDriveLetter = IsDriveLetter(mountPoint);

            //If a live volume currently answers at this letter, it belongs to someone - possibly
            //another clonezilla-util mid-serve. Never RemoveMountPoint it: that destroys the other
            //process's mount out from under its open handles (2026-07-17: a slow ListContents
            //process did exactly this to the suite's L: mount mid-copy - Investigation I2). Report
            //the letter as unavailable instead; the caller retries elsewhere or fails fast. (Not for a
            //folder: an empty folder is exactly what Dokan needs there, so it always "exists".)
            var probeRoot = mountPoint.TrimEnd('\\') + "\\";
            if (isDriveLetter && Directory.Exists(probeRoot))
            {
                failureReason = "a live volume already answers at this mount point; refusing to displace it";
                return null;
            }

            //A folder mount point whose process was killed (or crashed) before unmounting stays behind as a dead
            //volume mount point: the folder can't be opened, and Dokan can't mount there again. Nothing answers
            //behind it, so turning it back into the plain folder harms nobody - the folder analogue of clearing
            //a dead Dokan letter below.
            if (!isDriveLetter && libDokan.Utility.IsDeadVolumeMountPoint(mountPoint))
            {
                if (!libDokan.Utility.TryClearVolumeMountPoint(mountPoint, out var clearError))
                {
                    failureReason = $"it is a dead mount point left by an earlier run that did not unmount, and clearing it failed: {clearError}";
                    return null;
                }
                Log.Information($"{mountPoint} was a dead mount point left by an earlier run that did not unmount; cleared it.");
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

            var mountTask = Task.Factory.StartNew(() =>
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
                            //(which surfaces to the app as 0x800705AA); 20s is generous for one read. Shared
                            //with DokanVFS's read watchdog, which extends slow reads by the same amount.
                            options.TimeOut = TimeSpan.FromMilliseconds(DokanVFS.OperationTimeoutMs);

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

            //A drive letter appears only once mounted, so wait for it first. A folder exists before the
            //mount, so for it the sentinel below appearing is the only sign the mount is up.
            if (isDriveLetter && !Utility.WaitForFolderToExist(mountPoint, TimeSpan.FromSeconds(30)))
            {
                failureReason = "the mount point did not appear within 30 seconds (the Dokan mount likely failed; see any errors above)";
                stopMount.Set();
                return null;
            }

            //the letter exists - but is the volume behind it OURS? Probe the sentinel that only this
            //VFS instance contains. Briefly retried, in case our own mount is still settling; a folder
            //mount point gets the full mount wait here instead (see above).
            var mountRoot = mountPoint.TrimEnd('\\') + "\\";
            var sentinelPath = Path.Combine(mountRoot, sentinelName);
            var sentinelDeadline = DateTime.UtcNow + (isDriveLetter ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(30));
            var sentinelVisible = false;
            while (true)
            {
                try { sentinelVisible = Directory.Exists(sentinelPath); } catch { }
                if (sentinelVisible || DateTime.UtcNow > sentinelDeadline) break;
                Thread.Sleep(100);
            }

            if (!sentinelVisible && !isDriveLetter)
            {
                failureReason = "the mount did not appear on the folder within 30 seconds (the Dokan mount likely failed; see any errors above)";
                stopMount.Set();
                return null;
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

            activeStopMount = stopMount;
            activeMountTask = mountTask;
            failureReason = "";
            return root;
        }

        //Mount points reach here normalised: a letter is always "L:\"; anything else is a folder.
        static bool IsDriveLetter(string mountPoint) => mountPoint.Length == 3 && mountPoint[1] == ':' && mountPoint[2] == '\\';

        //The live mount's stop signal and serving task (set once TryMount succeeds), for Unmount.
        ManualResetEvent? activeStopMount;
        Task? activeMountTask;

        /// <summary>
        /// Stops serving, waits (up to 15 s) for Dokan to unmount, and leaves a folder mount point as the plain
        /// folder it was. Process exit drops the volume anyway, but not a FOLDER mount point's reparse point: even a
        /// clean Dokan unmount leaves that behind when unelevated (observed with Dokan 2.3.1), and the folder then
        /// can't be opened until the next mount clears it (gh review F51).
        /// </summary>
        public void Unmount()
        {
            var stopMount = Interlocked.Exchange(ref activeStopMount, null);
            if (stopMount == null) return;   //never mounted, or already unmounted

            stopMount.Set();
            if (activeMountTask != null && !activeMountTask.Wait(TimeSpan.FromSeconds(15)))
            {
                Log.Warning($"The virtual file system at {MountPoint} did not unmount within 15 seconds; exiting anyway.");
                return;
            }

            if (!IsDriveLetter(MountPoint) && libDokan.Utility.IsDeadVolumeMountPoint(MountPoint))
            {
                if (libDokan.Utility.TryClearVolumeMountPoint(MountPoint, out var clearError))
                    Log.Debug($"Unmounted {MountPoint} and restored the plain folder.");
                else
                    Log.Warning($"Unmounted {MountPoint}, but could not restore the plain folder ({clearError}); the next mount there will clear it.");
            }
        }

        public string ProgramName
        { get; }
        public string MountPoint { get; private set; }
        public bool AllowMountPointFallback { get; }
        public Lazy<RootFolder> RootFolder { get; }
        public Lazy<Folder> TempFolderRoot { get; }

        public Folder CreateTempFolder()
        {
            var tempFolderName = Path.GetFileNameWithoutExtension(Path.GetRandomFileName());
            var tempFolder = new Folder(tempFolderName, TempFolderRoot.Value);

            return tempFolder;
        }
    }
}
