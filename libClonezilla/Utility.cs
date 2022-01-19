using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Linq;
using libClonezilla.PartitionContainers;
using libDokan.VFS.Folders;
using libDokan;
using Serilog;
using System.Threading.Tasks;
using System.Threading;
using DokanNet;

namespace libClonezilla
{
    public static class Utility
    {
        public static void LoadAllBinDirectoryAssemblies(string folder)
        {
            foreach (string dll in Directory.GetFiles(folder, "*.dll", SearchOption.AllDirectories))
            {
                try
                {
                    //Assembly loadedAssembly = Assembly.LoadFile(dll);
                    AppDomain.CurrentDomain.Load(Assembly.LoadFrom(dll).GetName());
                }
                catch (FileLoadException)
                { } // The Assembly has already been loaded.
                catch (BadImageFormatException)
                { } // If a BadImageFormatException exception is thrown, the file is not an assembly.

            }
        }

        public static void MountPartitionsAsImageFiles(string programName, PartitionContainer container, string mountPoint, Folder containersRoot, Folder rootToMount) =>
            MountPartitionsAsImageFiles(
                programName, 
                new List<PartitionContainer>() { container}, 
                mountPoint, 
                containersRoot, 
                rootToMount);

            public static void MountPartitionsAsImageFiles(string programName, List<PartitionContainer> containers, string mountPoint, Folder containersRoot, Folder rootToMount)
        {
            //tell each partition to create a virtual file
            containers
                .ForEach(container =>
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

                    container
                        .Partitions
                        .ForEach(partition =>
                        {
                            partition.AddPartitionImageToVirtualFolder(mountPoint, containerFolder);
                        });
                });

            var vfs = new DokanVFS(programName, rootToMount);
            Task.Factory.StartNew(() => vfs.Mount(mountPoint, DokanOptions.WriteProtection, 64, new DokanNet.Logging.NullLogger()));
            WaitForMountPointToBeAvailable(mountPoint);
        }

        private static void WaitForMountPointToBeAvailable(string mountPoint)
        {
            Log.Information($"Waiting for {mountPoint} to be available.");
            while (true)
            {
                if (Directory.Exists(mountPoint))
                {
                    break;
                }
                Thread.Sleep(100);
            }
        }
    }
}
