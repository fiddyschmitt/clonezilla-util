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

        //one reusable stream for the memory-mapped (paging) read path, which the OS issues with no file
        //handle context - so we can't stash a per-handle stream as we do for normal reads
        Stream? memoryMappedStream;
        readonly object memoryMappedStreamLock = new();

        /// <summary>
        /// Serves a memory-mapped (paging) read at an absolute offset. These arrive with no handle context,
        /// so rather than opening (and disposing) a stream per page fault we keep one reusable stream per
        /// file. Concurrent paging reads of the same file are serialised; different files are independent.
        /// </summary>
        public int ReadForMemoryMap(byte[] buffer, long offset, int count)
        {
            if (CreatesNewStreamPerCall)
            {
                //a stream of our own (independent of any open handle); reuse it across page faults instead
                //of re-creating one each time. Such streams hold no scarce resource (e.g. the native
                //extractor's PooledNativeItemStream borrows a worker only per read), so keeping one is cheap.
                lock (memoryMappedStreamLock)
                {
                    memoryMappedStream ??= GetStream();
                    memoryMappedStream.Position = offset;
                    return memoryMappedStream.Read(buffer, 0, count);
                }
            }

            //one shared stream backs this file (same object normal reads use), so serialise on ReadLock
            lock (ReadLock)
            {
                var stream = GetStream();
                stream.Position = offset;
                return stream.Read(buffer, 0, count);
            }
        }

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
