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
    public class UbuntuFileSystems
    {
        [TestMethod]
        public void ext4()
        {
            if (!Main.RunLargeTests)
            {
                Assert.Inconclusive($"Not run. ({nameof(Main.RunLargeTests)} = False)");
                return;
            }

            //default Ubuntu file system (ext4)
            ConfirmFilesExist(
                Main.ExeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\clonezilla images\2022-09-13-12-img_ubuntu_22.04_default_filesystem" --mount L:\ """,
                new[] {
                    new FileDetails(@"L:\sda2\EFI\ubuntu\grub.cfg", "51f2a19ab5455fc3b7e2e2a0af00b9c0"),
                    new FileDetails(@"L:\sda3\etc\fstab", "ea6ab8635f91425f7b1566a2d2f9675b"),
                });
        }

        [TestMethod]
        public void ext4_lvm()
        {
            if (!Main.RunLargeTests)
            {
                Assert.Inconclusive($"Not run. ({nameof(Main.RunLargeTests)} = False)");
                return;
            }

            //Ubuntu installed using LVM file system
            ConfirmFilesExist(
                Main.ExeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\clonezilla images\2022-09-13-12-img_ubuntu_22.04_lvm_filesystem" --mount L:\ """,
                new[] {
                    new FileDetails(@"L:\sda2\EFI\ubuntu\grub.cfg", "3d20729047129a9315aad73359090eab"),
                    new FileDetails(@"L:\vgubuntu-root\etc\fstab", "8aa06a520842b37790b91ebe67ccba06"),
                });
        }
    }
}
