// [clonezilla-util addition] Minimal shims for the three Deflater-side members the vendored
// inflater references, so the (much larger) Deflater sources don't need to be vendored too.
// Values/implementation match upstream SharpZipLib v1.4.2 (Deflater.cs, DeflaterConstants.cs,
// DeflaterHuffman.cs), MIT licensed.

namespace libGZip.Vendored.SharpZipLib.Zip.Compression
{
	internal static class Deflater
	{
		public const int DEFLATED = 8;
	}

	internal static class DeflaterConstants
	{
		public const int STORED_BLOCK = 0;
		public const int STATIC_TREES = 1;
		public const int DYN_TREES = 2;
	}

	internal static class DeflaterHuffman
	{
		private static readonly short[] bit4Reverse = { 0, 8, 4, 12, 2, 10, 6, 14, 1, 9, 5, 13, 3, 11, 7, 15 };

		public static short BitReverse(int toReverse)
		{
			return (short)(bit4Reverse[toReverse & 0xF] << 12 |
						   bit4Reverse[(toReverse >> 4) & 0xF] << 8 |
						   bit4Reverse[(toReverse >> 8) & 0xF] << 4 |
						   bit4Reverse[toReverse >> 12]);
		}
	}
}
