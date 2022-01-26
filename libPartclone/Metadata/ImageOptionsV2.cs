using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libPartclone.Metadata
{
    public class ImageOptionsV2
    {
		/// Number of bytes used by this struct
		public uint FeatureSize;

		/// version of the image
		public ushort ImageVersion;

		/// partclone's compilation architecture: 32 bits or 64 bits
		public ushort CpuBits;

		/// checksum algorithm used (see checksum_mode_enum)
		public CrcModeEnum ChecksumMode;

		/// Size of one checksum, in bytes. 0 when NONE, 4 with CRC32, etc.
		public ushort ChecksumSize;

		/// How many consecutive blocks are checksumed together.
		public uint BlocksPerChecksum;

		/// Reseed the checksum after each write (1 = yes; 0 = no)
		public byte ReseedChecksum;

		/// Kind of bitmap stored in the image (see bitmap_mode_enum)
		public BitmapMode BitmapMode;

		public ImageOptionsV2()
        {

        }

		public ImageOptionsV2(BinaryReader binaryReader)
		{
			FeatureSize = binaryReader.ReadUInt32();

			ImageVersion = binaryReader.ReadUInt16();
			CpuBits = binaryReader.ReadUInt16();
			ChecksumMode = (CrcModeEnum)binaryReader.ReadUInt16();
			ChecksumSize = binaryReader.ReadUInt16();

			BlocksPerChecksum = binaryReader.ReadUInt32();

			ReseedChecksum = binaryReader.ReadByte();
			BitmapMode = (BitmapMode)binaryReader.ReadByte();
		}

		public override string ToString()
		{
			var result = $@"
FeatureSize: {FeatureSize}
ImageVersion: {ImageVersion}
CpuBits: {CpuBits}
ChecksumMode: {ChecksumMode}
ChecksumSize: {ChecksumSize}
BlocksPerChecksum: {BlocksPerChecksum}
ReseedChecksum: {ReseedChecksum}
BitmapMode: {BitmapMode}

".Trim();

			return result;
		}
	}

	public enum BitmapMode
	{
		BM_NONE = 0x00,
		BM_BIT = 0x01,
		BM_BYTE = 0x08,
	}

	public enum CrcModeEnum
	{
		CSM_NONE = 0x00,
		CSM_CRC32 = 0x20,
		CSM_CRC32_0001 = 0xFF,
	}
}
