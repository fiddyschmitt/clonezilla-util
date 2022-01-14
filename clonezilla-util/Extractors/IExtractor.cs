using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace clonezilla_util.Extractors
{
    public interface IExtractor
    {
        Stream Extract(string path);
    }
}
