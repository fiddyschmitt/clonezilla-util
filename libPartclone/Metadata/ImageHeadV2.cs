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

        /// 0xC0DE = little-endian, 0xDEC0 = big-endian
        public bool BigEndian;

        public ImageHeadV2()
        {

        }

        public ImageHeadV2(BinaryReader binaryReader)
        {
            Magic = Encoding.ASCII.GetString(binaryReader.ReadBytes(16)).TrimEnd('\0');
            PartcloneVersion = Encoding.ASCII.GetString(binaryReader.ReadBytes(14)).TrimEnd('\0');
            ImageVersion = Encoding.ASCII.GetString(binaryReader.ReadBytes(4)).TrimEnd('\0');

            ushort endianess = binaryReader.ReadUInt16();
            //ushort endianess = BitConverter.ToUInt16(binaryReader.ReadBytes(2).Reverse().ToArray(), 0);
            BigEndian = endianess == 0xC0DE;
        }

        public override string ToString()
        {
            var lines = new[]
            {
                $"Magic: { Magic}",
                $"PartcloneVersion: {PartcloneVersion}",
                $"ImageVersion: {ImageVersion}",
                $"Endianess: {(BigEndian ? "Big Endian" : "Little Endian")}"
                };

            string result = lines.ToString(Environment.NewLine);

            return result;
        }
    }
}
