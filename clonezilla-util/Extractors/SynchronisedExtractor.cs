using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace clonezilla_util.Extractors
{
    public class SynchronisedExtractor : IExtractor
    {
        public SynchronisedExtractor(IExtractor baseExtractor)
        {
            BaseExtractor = baseExtractor;
        }

        public IExtractor BaseExtractor { get; }

        public Stream Extract(string path)
        {
            Stream stream = BaseExtractor.Extract(path);

            //protect it from concurrent reads
            stream = Stream.Synchronized(stream);

            return stream;
        }
    }
}
