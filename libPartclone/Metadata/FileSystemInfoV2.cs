using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libPartclone.Metadata
{
    public class FileSystemInfoV2
    {
		public string? FileSystemType;

		/// Size of the source device, in bytes
		public ulong DeviceSizeBytes;

		/// Number of blocks in the file system
		public ulong TotalBlocks;

		/// Number of blocks in use as reported by the file system
		public ulong UsedBlocks;

		/// Number of blocks in use within the bitmap
		public ulong UsedBitmapBlocks;

		/// Number of bytes in each block
		public uint BlockSize;

		public FileSystemInfoV2()
        {

        }

		public FileSystemInfoV2(BinaryReader binaryReader)
		{
			FileSystemType = Encoding.ASCII.GetString(binaryReader.ReadBytes(16)).TrimEnd('\0');

			DeviceSizeBytes = binaryReader.ReadUInt64();
			TotalBlocks = binaryReader.ReadUInt64();
			UsedBlocks = binaryReader.ReadUInt64();
			UsedBitmapBlocks = binaryReader.ReadUInt64();

			BlockSize = binaryReader.ReadUInt32();
		}

		public override string ToString()
		{
			var result = $@"
FileSystemType: {FileSystemType}
DeviceSize: {DeviceSizeBytes:N0} ({DeviceSizeBytes.BytesToString()})
TotalBlocks: {TotalBlocks:N0}
UsedBlocks: {UsedBlocks:N0}
UsedBitmap: {UsedBitmapBlocks:N0}
BlockSize: {BlockSize:N0} ({((ulong)BlockSize).BytesToString()})
".Trim();

			return result;
		}
	}
}
