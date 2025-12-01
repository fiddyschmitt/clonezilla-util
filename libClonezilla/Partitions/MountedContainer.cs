using libClonezilla.PartitionContainers;
using libClonezilla.VFS;
using libDokan.VFS;
using libDokan.VFS.Files;
using libDokan.VFS.Folders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static libClonezilla.Partitions.MountedPartitionImage;

namespace libClonezilla.Partitions
{
    public class MountedContainer
    {
        public MountedContainer(PartitionContainer container, Folder containerFolder, Lazy<IVFS> vfs, DesiredContent desiredContent)
        {
            Container = container;

            MountedPartitions = container
                                    .Partitions
                                    .Select(partition =>
                                    {
                                        Folder partitionFolder;

                                        if (container.Partitions.Count == 1 || desiredContent == DesiredContent.ImageFiles)
                                        {
                                            partitionFolder = containerFolder;
                                        }
                                        else
                                        {
                                            partitionFolder = new Folder(partition.PartitionName, containerFolder);
                                        }

                                        var mountedPartition = new MountedPartitionImage(partition, partitionFolder, vfs, desiredContent);
                                        return mountedPartition;
                                    })
                                    .ToList();
        }

        public PartitionContainer Container { get; }

        public List<MountedPartitionImage> MountedPartitions;
    }
}
