using System.IO;

namespace libPartclone.Metadata
{
    public class ImageOptionsV1
    {
        public byte[] Buff = new byte[4096];

        public ImageOptionsV1()
        {

        }

        public ImageOptionsV1(BinaryReader binaryReader)
        {
            Buff = binaryReader.ReadBytes(Buff.Length);
        }

        public override string ToString()
        {
            var result = $@"
Buff Length: {Buff.Length}
".Trim();

            return result;
        }
    }
}