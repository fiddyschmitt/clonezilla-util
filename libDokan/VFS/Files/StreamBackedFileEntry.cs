using libDokan.VFS.Folders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libDokan.VFS.Files
{
    public class StreamBackedFileEntry : FileEntry
    {
        //one shared stream serves every handle (a stream-factory variant, used only by a removed test harness, is gone)
        readonly Stream Stream;

        public StreamBackedFileEntry(string name, Folder? folder, Stream stream) : base(name, folder)
        {
            Stream = stream;
            Length = stream.Length;
        }

        public override bool CreatesNewStreamPerCall => false;

        public override Stream GetStream() => Stream;
    }
}
