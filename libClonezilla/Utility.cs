using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Linq;

namespace libClonezilla
{
    public static class Utility
    {
        public static void LoadAllBinDirectoryAssemblies(string folder)
        {
            foreach (string dll in Directory.GetFiles(folder, "*.dll", SearchOption.AllDirectories))
            {
                try
                {
                    //Assembly loadedAssembly = Assembly.LoadFile(dll);
                    AppDomain.CurrentDomain.Load(Assembly.LoadFrom(dll).GetName());
                }
                catch (FileLoadException)
                { } // The Assembly has already been loaded.
                catch (BadImageFormatException)
                { } // If a BadImageFormatException exception is thrown, the file is not an assembly.

            }
        }
    }
}
