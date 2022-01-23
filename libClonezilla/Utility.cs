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
using libClonezilla.Partitions;
using libClonezilla.VFS;
using static libClonezilla.Partitions.MountedPartitionImage;

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

        public static List<MountedContainer> PopulateVFS(IVFS vfs, Folder containersRoot, List<PartitionContainer> containers, DesiredContent desiredContent)
        {
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

        public static void WaitForFolderToExist(string folderPath)
        {
            Log.Information($"Waiting for {folderPath} to be available.");
            while (true)
            {
                if (Directory.Exists(folderPath))
                {
                    break;
                }
                Thread.Sleep(100);
            }
        }
    }
}
