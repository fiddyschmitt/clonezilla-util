using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static clonezilla_util_tests.Mount.TestUtility;

namespace clonezilla_util_tests.Mount.AsFiles
{
    [TestClass]
    [DoNotParallelize]
    public class Ext4
    {
        [TestMethod]
        public void ext4()
        {
            ConfirmFilesExist(
                Main.ExeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\drive images\ext4\2022-08-16_ext4.img" --mount L:\ """,
                new[] {
                    new FileDetails(@"L:\hello.txt", "bad9425ff652b1bd52b49720abecf0ba"),
                    new FileDetails(@"L:\config_files\test.xml", "81dfa8e288df74cebc654c582a3abebc"),
                });
        }

        [TestMethod]
        public void ext4_zst()
        {
            //Takes a long time because 7z.exe tries to scan across the entire 33 GB file, requiring the zstandard stream (even though only 1 MB) to be opened over and over by SeekableStreamUsingRestarts
            if (!Main.RunLargeTests)
            {
                Assert.Inconclusive($"Not run. ({nameof(Main.RunLargeTests)} = False)");
                return;
            }

            ConfirmFilesExist(
                Main.ExeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\drive images\ext4\2022-08-16_ext4.img.zst" --mount L:\ """,
                new[] {
                    new FileDetails(@"L:\hello.txt", "bad9425ff652b1bd52b49720abecf0ba"),
                    new FileDetails(@"L:\config_files\test.xml", "81dfa8e288df74cebc654c582a3abebc"),
                });
        }
    }
}
