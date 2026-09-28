using System.Runtime.InteropServices;

namespace clonezilla_util_tests.Mount.AsFiles
{
    // Directory searches with wildcards against a mounted drive. Win32's FindFirstFile hands the pattern to the file
    // system in NT form, rewriting '?' as '>', '*' before a '.' as '<', and a '.' before a wildcard as '"' (the DOS
    // wildcards); listings went empty for exactly those until the VFS matched with DokanIsNameInExpression (gh review F6).
    // .NET's Directory.GetFiles filters in managed code instead, so the searches here go through FindFirstFileW itself.
    [TestClass]
    [DoNotParallelize]
    public class SearchPatterns
    {
        [TestMethod]
        public void WildcardSearches()
        {
            TestUtility.WhileMounted(
                Main.ExeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\clonezilla images\2022-06-27-20-img_small_drive-uncompressed" -m L:\""",
                @"L:\",
                _ =>
                {
                    var failures = new List<string>();
                    void Expect(string pattern, params string[] expected)
                    {
                        var found = Find(@"L:\sda1\" + pattern);
                        if (!found.SetEquals(expected))
                        {
                            failures.Add($"'{pattern}': expected [{string.Join(", ", expected)}], found [{string.Join(", ", found)}]");
                        }
                    }

                    Expect("sda1.txt", "sda1.txt");
                    Expect("SDA1.TXT", "sda1.txt");        //case-insensitive, like NTFS
                    Expect("sda1.tx?", "sda1.txt");        //sent as "sda1.tx>"
                    Expect("sda?.txt", "sda1.txt");        //sent as "sda>.txt"
                    Expect("sda1.???", "sda1.txt");        //sent as "sda1\">>>"
                    Expect("*.txt", "sda1.txt");           //sent as "<.txt"
                    Expect("s*1.t*", "sda1.txt");
                    Expect("sda1.tx?x");                   //one character too many
                    Expect("*.xyz");

                    Assert.AreEqual(0, failures.Count, "searches that returned the wrong entries:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
                });
        }

        //the names FindFirstFileW/FindNextFileW return for `pattern` ("." and ".." left out), case-insensitively
        static HashSet<string> Find(string pattern)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var handle = FindFirstFileW(pattern, out var data);
            if (handle == InvalidHandleValue)
            {
                var error = Marshal.GetLastWin32Error();
                if (error is ErrorFileNotFound or ErrorNoMoreFiles) return names;
                throw new System.ComponentModel.Win32Exception(error, $"FindFirstFileW(\"{pattern}\") failed");
            }

            try
            {
                do
                {
                    if (data.cFileName is not "." and not "..") names.Add(data.cFileName);
                } while (FindNextFileW(handle, out data));
            }
            finally
            {
                FindClose(handle);
            }
            return names;
        }

        const int ErrorFileNotFound = 2;
        const int ErrorNoMoreFiles = 18;
        static readonly IntPtr InvalidHandleValue = new(-1);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct Win32FindData
        {
            public uint dwFileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint dwReserved0;
            public uint dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr FindFirstFileW(string lpFileName, out Win32FindData lpFindFileData);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool FindNextFileW(IntPtr hFindFile, out Win32FindData lpFindFileData);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool FindClose(IntPtr hFindFile);
    }
}
