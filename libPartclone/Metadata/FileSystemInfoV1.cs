using System.IO;

namespace libPartclone.Metadata
{
    public class FileSystemInfoV1
    {
        public uint BlockSize;
        public ulong DeviceSizeBytes;
        public ulong TotalBlocks;
        public ulong UsedBlocks;

		public FileSystemInfoV1()
        {

        }

		public FileSystemInfoV1(BinaryReader binaryReader)
		{
			BlockSize = binaryReader.ReadUInt32();
			DeviceSizeBytes = binaryReader.ReadUInt64();
			TotalBlocks = binaryReader.ReadUInt64();
			UsedBlocks = binaryReader.ReadUInt64();
		}

		public override string ToString()
		{
			var result = $@"
BlockSize: {BlockSize}
DeviceSize: {DeviceSizeBytes:N0} ({DeviceSizeBytes.BytesToString()})
TotalBlocks: {TotalBlocks:N0}
UsedBlocks: {UsedBlocks:N0}
".Trim();

			return result;
		}
	}
}