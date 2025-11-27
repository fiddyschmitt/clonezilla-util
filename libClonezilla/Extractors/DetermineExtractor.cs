using lib7Zip;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace libClonezilla.Extractors
{
    public static class DetermineExtractor
    {
        public static IExtractor FindExtractor(string path)
        {
            //List<IExtractor> candidates = [
            //    new ExtractorUsing7zip(),
            //    //new XfsExtractor()      //FPS 23/11/2025: This library can list files, but cannot extract files from XFSv5 (which uses CRC=1). And there are reports of XFSv4 not working either.
            //    ];

            //extractor = candidates
            //            .FirstOrDefault(candidate => candidate.Initialise(path));

            //For now, we only support extracting using 7z, so short-circuit to that.
            var result = new ExtractorUsing7zip(path);

            return result;
        }
    }
}
