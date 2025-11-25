using lib7Zip;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace libClonezilla.Extractors
{
    public class DetermineExtractor : IExtractor, IFileListProvider
    {
        IExtractor? extractor;

        public bool Initialise(string path)
        {
            List<IExtractor> candidates = [
                new ExtractorUsing7zip(),
                //new XfsExtractor()      //FPS 23/11/2025: This library can list files, but cannot extract files from XFSv5 (which uses CRC=1). And there are reports of XFSv4 not working either.
                ];

            extractor = candidates
                        .FirstOrDefault(candidate => candidate.Initialise(path));

            var result = extractor != null;
            return result;
        }

        public Stream Extract(string path)
        {
            if (extractor == null)
            {
                return Stream.Null;
            }

            var result = extractor.Extract(path);

            return result;
        }

        public IEnumerable<ArchiveEntry> GetFileList()
        {
            IEnumerable<ArchiveEntry> result;
            if (extractor is IFileListProvider fileListProvider)
            {
                result = fileListProvider.GetFileList();
            }
            else
            {
                result = [];
            }

            return result;
        }
    }
}
