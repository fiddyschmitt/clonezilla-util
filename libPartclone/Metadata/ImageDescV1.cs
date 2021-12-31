using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace libPartclone.Metadata
{
    public class ImageDescV1
    {
        public ImageHeadV1? ImageHeadV1;
        public FileSystemInfoV1? FileSystemInfoV1;
        public ImageOptionsV1? ImageOptionsV1;

        public ImageDescV1()
        {

        }

        public ImageDescV1(BinaryReader binaryReader)
        {
            ImageHeadV1 = new ImageHeadV1(binaryReader);
            FileSystemInfoV1 = new FileSystemInfoV1(binaryReader);
            ImageOptionsV1 = new ImageOptionsV1(binaryReader);
        }

        public override string ToString()
        {
            var imageHeadLines = ImageHeadV1?.ToString().ToLines();
            var fileSystemInfoLines = FileSystemInfoV1?.ToString().ToLines();
            var imageOptionsLines = ImageOptionsV1?.ToString().ToLines();

            var result = $@"
ImageDescV1

    ImageHeadV1
{imageHeadLines?.ToString("        ", Environment.NewLine)}

    FileSystemInfoV1
{fileSystemInfoLines?.ToString("        ", Environment.NewLine)}

    ImageOptionsV1
{imageOptionsLines?.ToString("        ", Environment.NewLine)}

".Trim();

            return result;
        }
    }
}

