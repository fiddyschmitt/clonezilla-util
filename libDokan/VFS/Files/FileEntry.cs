using DokanNet;
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

        public abstract Stream GetStream();

        public FileEntry(string name) : base(name)
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
