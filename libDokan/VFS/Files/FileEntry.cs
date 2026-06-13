using DokanNet;
using libDokan.VFS.Folders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libDokan.VFS.Files
{
    public abstract class FileEntry : FileSystemEntry
    {
        public long Length { get; set; }

        //serialises access to the stream(s) backing this file. GetStream() can return one shared stream
        //for every open handle, so seek+read pairs from different handles must not interleave.
        public readonly object ReadLock = new();

        //when true, each GetStream() call returns a new stream which the caller owns (and must dispose).
        //when false, GetStream() returns a single shared stream which must not be disposed by callers.
        public virtual bool CreatesNewStreamPerCall => true;

        public abstract Stream GetStream();

        public FileEntry(string name, Folder? parent) : base(name, parent)
        {
        }

        protected override FileInformation ToFileInfo()
        {
            var result = new FileInformation
            {
                Length = Length,
            };

            return result;
        }

        public override string ToString()
        {
            var result = $"{Name}";
            return result;
        }
    }
}
