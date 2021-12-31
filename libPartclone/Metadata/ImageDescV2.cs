using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libPartclone.Metadata
{
    public class ImageDescV2
    {
        public ImageHeadV2? ImageHeadV2;
        public FileSystemInfoV2? FileSystemInfoV2;
        public ImageOptionsV2? ImageOptionsV2;
        public uint CRC;

        public ImageDescV2()
        {

        }

        public ImageDescV2(BinaryReader binaryReader)
        {
            ImageHeadV2 = new ImageHeadV2(binaryReader);            //offset: 0x00
            FileSystemInfoV2 = new FileSystemInfoV2(binaryReader);  //offset: 0x24
            ImageOptionsV2 = new ImageOptionsV2(binaryReader);      //offset: 0x58
            CRC = binaryReader.ReadUInt32();                        //offset: 0x6A
        }

        public override string ToString()
        {
            var imageHeadLines = ImageHeadV2?.ToString().ToLines();
            var fileSystemInfoLines = FileSystemInfoV2?.ToString().ToLines();
            var imageOptionsLines = ImageOptionsV2?.ToString().ToLines();

            var result = $@"
ImageDescV2

    ImageHeadV2
{imageHeadLines?.ToString("        ", Environment.NewLine)}

    FileSystemInfoV2
{fileSystemInfoLines?.ToString("        ", Environment.NewLine)}

    ImageOptionsV2
{imageOptionsLines?.ToString("        ", Environment.NewLine)}

    CRC
        0x{CRC:X}


".Trim();

            return result;
        }
    }
}
