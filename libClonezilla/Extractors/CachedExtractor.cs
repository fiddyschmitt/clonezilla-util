using libCommon;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libClonezilla.Extractors
{
    public class CachedExtractor : IExtractor
    {
        ConcurrentDictionary<string, Stream> AlreadyExtracted = new();

        public CachedExtractor(IExtractor baseExtractor)
        {
            BaseExtractor = baseExtractor;
        }

        public IExtractor BaseExtractor { get; }

        public Stream Extract(string path)
        {
            var result = AlreadyExtracted
                            .GetOrAdd(path, path =>
                            {
                                Stream stream = BaseExtractor.Extract(path);

                                return stream;
                            });

            return result;
        }
    }
}
