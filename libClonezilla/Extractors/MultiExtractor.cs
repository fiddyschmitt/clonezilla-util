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
    public class MultiExtractor : IExtractor
    {
        public MultiExtractor(List<IExtractor> extractors, bool forceFullRead)
        {
            Extractors = [];

            extractors
                .ForEach(extractor => Extractors.Add(extractor));
            ForceFullRead = forceFullRead;
        }

        public BlockingCollection<IExtractor> Extractors { get; }
        public bool ForceFullRead { get; }

        public bool Initialise(string path)
        {
            //can't use this class directly
            return false;
        }

        public Stream Extract(string path)
        {
            //find an idle worker
            var worker = Extractors.GetConsumingEnumerable().First();

            //do the work
            var result = worker.Extract(path);

            if (ForceFullRead)
            {
                //some streams are non-blocking. We've been asked to read the stream in its entirety before calling it done
                result.CopyTo(Stream.Null, Buffers.ARBITARY_LARGE_SIZE_BUFFER);
            }

            //go to the back of the line
            Extractors.Add(worker);

            return result;
        }
    }
}
