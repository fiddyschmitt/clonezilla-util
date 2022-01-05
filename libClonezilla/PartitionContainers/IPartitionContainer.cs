using libClonezilla.Partitions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace libClonezilla.PartitionContainers
{
    public interface IPartitionContainer
    {
        List<Partition> Partitions { get; }

        static PartitionContainerType FromPath(string path)
        {
            PartitionContainerType? result = null;

            if (Directory.Exists(path))
            {
                var clonezillaMagicFile = Path.Combine(path, "clonezilla-img");
                if (File.Exists(clonezillaMagicFile))
                {
                    result = PartitionContainerType.ClonezillaFolder;
                }
            }
            else
            {
                if (File.Exists(path))
                {
                    try
                    {
                        using var fileStream = File.OpenRead(path);
                        using var binaryReader = new BinaryReader(fileStream);
                        var magic = Encoding.ASCII.GetString(binaryReader.ReadBytes(16)).TrimEnd('\0');

                        if (magic.Equals("partclone-image"))
                        {
                            result = PartitionContainerType.PartcloneFile;
                        }
                    }
                    catch { }
                }
            }

            if (result == null)
            {
                throw new Exception($"Could not determine if this is a Clonezilla folder, or a partclone file: {path}");
            }

            return result.Value;
        }
    }

    public enum PartitionContainerType
    {
        ClonezillaFolder,
        PartcloneFile
    }
}