using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace libPartclone.Metadata
{
    public class ImageHeadV1
    {
        //char magic[IMAGE_MAGIC_SIZE];
        public string? Magic;   //15 bytes

        //char fs[FS_MAGIC_SIZE];
        public string? FileSystem;  //15 bytes

        //char version[IMAGE_VERSION_SIZE];
        public string? ImageVersion;    //4 bytes

        //char padding[2];
        public string? Padding; //2 bytes

        public ImageHeadV1()
        {

        }

        public ImageHeadV1(BinaryReader binaryReader)
        {
            Magic = Encoding.ASCII.GetString(binaryReader.ReadBytes(15)).TrimEnd('\0');
            FileSystem = Encoding.ASCII.GetString(binaryReader.ReadBytes(15)).TrimEnd('\0');
            ImageVersion = Encoding.ASCII.GetString(binaryReader.ReadBytes(4)).TrimEnd('\0');
            Padding = Encoding.ASCII.GetString(binaryReader.ReadBytes(2)).TrimEnd('\0');
        }

        public override string ToString()
        {
            var lines = new[]
            {
                $"Magic: { Magic}",
                $"FileSystem: {FileSystem}",
                $"ImageVersion: {ImageVersion}",
                $"Padding: {Padding}"
                };

            string result = lines.ToString(Environment.NewLine);

            return result;
        }
    }
}
