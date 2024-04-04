using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace clonezilla_util_tests.ListContents
{
    [DoNotParallelize]
    [TestClass]
    public class Partclone
    {
        [TestMethod]
        public void MixedPartcloneFormats()
        {
            TestUtility.ConfirmContainsStrings(
                Main.ExeUnderTest,
                """list --input "E:\clonezilla-util-test resources\clonezilla images\2020-11-15-00-img_small_drive_new_partclone (clonezilla-live-2.6.6-15)" "E:\clonezilla-util-test resources\clonezilla images\2020-05-23-23-img_small_drive_old_partclone (clonezilla-live-2.5.2-31)" """,
                new[] {
                        @"2020-05-23-23-img_small_drive_old_partclone (clonezilla-live-2.5.2-31)\sda1\sda1.txt",
                        @"2020-05-23-23-img_small_drive_old_partclone (clonezilla-live-2.5.2-31)\sda2\sda2.txt",

                        @"2020-11-15-00-img_small_drive_new_partclone (clonezilla-live-2.6.6-15)\sda1\sda1.txt",
                        @"2020-11-15-00-img_small_drive_new_partclone (clonezilla-live-2.6.6-15)\sda2\sda2.txt"
                });

        }
    }
}
