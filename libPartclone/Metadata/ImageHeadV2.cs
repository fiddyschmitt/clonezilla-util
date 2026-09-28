using libCommon;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libPartclone.Metadata
{
    public class ImageHeadV2
    {
        public string? Magic;

        /// Partclone's version who created the image, ex: "2.61"
        public string? PartcloneVersion;

        /// Image's version
        public string? ImageVersion;

        /// Partclone writes ENDIAN_MAGIC (0xC0DE) in the writing machine's byte order. Read little-endian, that is
        /// 0xC0DE for an image written on a little-endian machine and 0xDEC0 for one written big-endian; anything
        /// else is not a v2 header. (This used to be decoded backwards, as BigEndian = marker == 0xC0DE, which only
        /// ever reached a debug log line.)
        public ushort EndiannessMarker;

        public const ushort LittleEndianMarker = 0xC0DE;
        public const ushort BigEndianMarker = 0xDEC0;

        public ImageHeadV2()
        {

        }

        public ImageHeadV2(BinaryReader binaryReader)
        {
            Magic = Encoding.ASCII.GetString(binaryReader.ReadBytes(16)).TrimEnd('\0');
            PartcloneVersion = Encoding.ASCII.GetString(binaryReader.ReadBytes(14)).TrimEnd('\0');
            ImageVersion = Encoding.ASCII.GetString(binaryReader.ReadBytes(4)).TrimEnd('\0');
            EndiannessMarker = binaryReader.ReadUInt16();
        }

        public override string ToString()
        {
            var endianness = EndiannessMarker switch
            {
                LittleEndianMarker => "Little Endian",
                BigEndianMarker => "Big Endian",
                _ => $"invalid marker 0x{EndiannessMarker:X4}",
            };

            string[] lines = [
                $"Magic: {Magic}",
                $"PartcloneVersion: {PartcloneVersion}",
                $"ImageVersion: {ImageVersion}",
                $"Endianess: {endianness}"
                ];

            string result = lines.ToString(Environment.NewLine);

            return result;
        }
    }
}
