using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libDokan.VFS.Files
{
    public class StreamBackedFileEntry : FileEntry
    {
        readonly Stream? Stream;
        readonly Func<Stream>? StreamFactory;

        public StreamBackedFileEntry(string name, Stream stream) : base(name)
        {
            Stream = stream;
        }

        public StreamBackedFileEntry(string name, Func<Stream> streamFactory) : base(name)
        {
            StreamFactory = streamFactory;
        }

        public override Stream GetStream()
        {
            Stream? result = null;

            if (Stream != null)
            {
                result = Stream;
            } else if (StreamFactory != null)
            {
                result = StreamFactory();
            }

            if (result == null) throw new Exception($"Could not retrieve a stream for {Name}");

            return result;
        }
    }
}
