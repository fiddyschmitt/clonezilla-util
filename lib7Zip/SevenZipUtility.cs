using libCommon;
using System.Runtime.InteropServices;

namespace lib7Zip
{
    public class SevenZipUtility
    {
        // The native engine (lib7zNative) loads 7z.dll for the format handlers/codecs. 7z.exe is no
        // longer used - all archive/filesystem opening, listing and extraction goes through the engine.
        public static string SevenZipDll()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Environment.Is64BitOperatingSystem) return Utility.Absolutify(@"ext\7-Zip\win-x64\7z.dll");
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && !Environment.Is64BitOperatingSystem) return Utility.Absolutify(@"ext\7-Zip\win-x86\7z.dll");

            throw new Exception("OS not supported yet.");
        }
    }
}
