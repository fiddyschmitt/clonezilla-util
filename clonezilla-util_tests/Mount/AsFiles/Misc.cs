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
    public class Misc
    {
        [TestMethod]
        public void MultipleContainers_MultiplePartitions()
        {
            ConfirmFilesExist(
                Main.ExeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\clonezilla images\2022-07-17-16-img_pb-devops1_gz" "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda1.img.zst" -p sda1 sdb1 partition0 --mount L:\ """,
                new[] {
                    new FileDetails(@"L:\2022-07-17-16-img_pb-devops1_gz\sda1\Recovery\Logs\Reload.xml", "f5a6df3c8f1ad69766afee3a25f7e376"),
                    new FileDetails(@"L:\2022-07-17-16-img_pb-devops1_gz\sdb1\Kingsley\Prototype 1\Temp\Images\logo.jpg", "0217ff1926ec5f82e1a120676eff70c3"),

                    new FileDetails(@"L:\2021-12-28_pb-devops1_sda1.img\Recovery\WindowsRE\ReAgent.xml", "464bd66c6443e55b791f16cb6bc28c2e")
                });
        }

        [TestMethod]
        public void LastestClonezilla_2022_06_29()
        {
            ConfirmFilesExist(
                Main.ExeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\clonezilla images\2022-06-27-20-img_small_drive" -m L:\ """,
                new[] {
                    new FileDetails(@"L:\sda1\sda1.txt", "c3f38733914d360530455ba3b4073868"),
                });
        }
    }
}
