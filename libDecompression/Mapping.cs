using libCommon;

namespace libDecompression
{
    /// <summary>One bzip2 block in a seekable-bzip2 index: where its compressed bits are and which uncompressed
    /// bytes it holds. Serialised into the index files, so field names are part of the index format.</summary>
    public class Mapping
    {
        public long CompressedStartByte;
        public long CompressedEndByte;  //0 in index files created before this field existed

        //Bit offset within the start/end byte (0-7, MSB-first). Non-zero only for bit-aligned bzip2
        //entries; absent (0) in gzip and older byte-aligned indexes, which then behave exactly as
        //before. Absolute bit offset = CompressedStartByte * 8 + CompressedStartBitInByte.
        public int CompressedStartBitInByte;
        public int CompressedEndBitInByte;

        public long UncompressedStartByte;
        public long UncompressedEndByte;

        public override string ToString()
        {
            var uncompressedSize = (ulong)(UncompressedEndByte - UncompressedStartByte);
            var uncompressedSizeStr = uncompressedSize.BytesToString();
            string result = $"Compressed {CompressedStartByte:N0} == Uncompressed {UncompressedStartByte:N0} ({uncompressedSizeStr} uncompressed data)";
            return result;
        }
    }
}
