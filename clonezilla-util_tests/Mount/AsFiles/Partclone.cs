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
    public class Partclone
    {
        [TestMethod]
        public void PartcloneImage()
        {
            ConfirmFilesExist(
                Main.ExeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\partclone images\2021-12-29 - partclone file\sdb1.ntfs-ptcl-img" -m L:\ """,
                new[] {
                    new FileDetails(@"L:\Kingsley\Prototype 1\Temp\Images\logo.jpg", "0217ff1926ec5f82e1a120676eff70c3"),
                });
        }

        [TestMethod]
        public void gz()
        {
            ConfirmFilesExist(
                Main.ExeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\partclone images\2022-07-17 - partclone file\sda1.ntfs-ptcl-img.gz" -m L:\ """,
                new[] {
                    new FileDetails(@"L:\Recovery\Logs\Reload.xml", "f5a6df3c8f1ad69766afee3a25f7e376"),
                });
        }

        [TestMethod]
        public void dd()
        {
            //clonezilla (partclone.dd + gz)
            ConfirmFilesExist(
                Main.ExeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\clonezilla images\2023-11-17-00-img_PB-DEVOPS1_using_dd" -m L:\""",
                new[] {
                    new FileDetails(@"L:\sda1\Recovery\Logs\Reload.xml", "f5a6df3c8f1ad69766afee3a25f7e376"),
                    new FileDetails(@"L:\sda2\Program Files\PostgreSQL\13\share\information_schema.sql", "97a4aa50fa682b00457810d010ff5852"),
                    new FileDetails(@"L:\sdb1\Kingsley\Prototype 1\Temp\Images\logo.jpg", "0217ff1926ec5f82e1a120676eff70c3"),
                });
        }

        [TestMethod]
        public void MixedPartcloneFormats()
        {
            //two different clonezilla formats
            ConfirmFilesExist(
                Main.ExeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\clonezilla images\2020-11-15-00-img_small_drive_new_partclone (clonezilla-live-2.6.6-15)" "E:\clonezilla-util-test resources\clonezilla images\2020-05-23-23-img_small_drive_old_partclone (clonezilla-live-2.5.2-31)" -m L:\ """,
                new[] {
                    new FileDetails(@"L:\2020-05-23-23-img_small_drive_old_partclone (clonezilla-live-2.5.2-31)\sda1\sda1.txt", "c3f38733914d360530455ba3b4073868"),
                    new FileDetails(@"L:\2020-05-23-23-img_small_drive_old_partclone (clonezilla-live-2.5.2-31)\sda2\sda2.txt", "b80328235f5d991c6dc8982e1d1876bc"),

                    new FileDetails(@"L:\2020-11-15-00-img_small_drive_new_partclone (clonezilla-live-2.6.6-15)\sda1\sda1.txt", "c3f38733914d360530455ba3b4073868"),
                    new FileDetails(@"L:\2020-11-15-00-img_small_drive_new_partclone (clonezilla-live-2.6.6-15)\sda2\sda2.txt", "b80328235f5d991c6dc8982e1d1876bc"),
                });
        }
    }
}