using lib7Zip;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace libClonezilla.Extractors
{
    public interface IExtractor
    {
        bool Initialise(string path);
        Stream Extract(string path);
    }
}
