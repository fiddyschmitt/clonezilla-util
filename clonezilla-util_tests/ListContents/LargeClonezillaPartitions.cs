using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace clonezilla_util_tests.ListContents
{
    [DoNotParallelize]
    [TestClass]
    public class LargeClonezillaPartitions
    {
        [TestMethod]
        public void Bzip2()
        {
            if (!Main.RunLargeTests)
            {
                Assert.Inconclusive($"Not run. ({nameof(Main.RunLargeTests)} = False)");
                return;
            }

            TestUtility.ConfirmContainsStrings(
                Main.ExeUnderTest,
                """list --input "E:\clonezilla-util-test resources\clonezilla images\2022-07-16-22-img_pb-devops1_bzip2""",
                new[] {
                    @"2022-07-16-22-img_pb-devops1_bzip2\sda1\Recovery\Logs\Reload.xml",
                    @"2022-07-16-22-img_pb-devops1_bzip2\sda2\Program Files\PostgreSQL\13\share\information_schema.sql",
                    @"2022-07-16-22-img_pb-devops1_bzip2\sdb1\Kingsley\Prototype 1\Temp\Images\logo.jpg"
                });
        }

        [TestMethod]
        public void gz()
        {
            if (!Main.RunLargeTests)
            {
                Assert.Inconclusive($"Not run. ({nameof(Main.RunLargeTests)} = False)");
                return;
            }

            TestUtility.ConfirmContainsStrings(
                Main.ExeUnderTest,
                """list --input "E:\clonezilla-util-test resources\clonezilla images\2022-07-17-16-img_pb-devops1_gz""",
                new[] {
                    @"2022-07-17-16-img_pb-devops1_gz\sda1\Recovery\Logs\Reload.xml",
                    @"2022-07-17-16-img_pb-devops1_gz\sda2\Program Files\PostgreSQL\13\share\information_schema.sql",
                    @"2022-07-17-16-img_pb-devops1_gz\sdb1\Kingsley\Prototype 1\Temp\Images\logo.jpg"
                });
        }

        [TestMethod]
        public void xz()
        {
            if (!Main.RunLargeTests)
            {
                Assert.Inconclusive($"Not run. ({nameof(Main.RunLargeTests)} = False)");
                return;
            }

            TestUtility.ConfirmContainsStrings(
                Main.ExeUnderTest,
                """list --input "E:\clonezilla-util-test resources\clonezilla images\2022-07-17-12-img_pb-devops1_xz""",
                new[] {
                    @"2022-07-17-12-img_pb-devops1_xz\sda1\Recovery\Logs\Reload.xml",
                    @"2022-07-17-12-img_pb-devops1_xz\sda2\Program Files\PostgreSQL\13\share\information_schema.sql",
                    @"2022-07-17-12-img_pb-devops1_xz\sdb1\Kingsley\Prototype 1\Temp\Images\logo.jpg"
                });
        }

        [TestMethod]
        public void zst()
        {
            if (!Main.RunLargeTests)
            {
                Assert.Inconclusive($"Not run. ({nameof(Main.RunLargeTests)} = False)");
                return;
            }

            TestUtility.ConfirmContainsStrings(
                Main.ExeUnderTest,
                """list --input "E:\clonezilla-util-test resources\clonezilla images\2022-07-16-22-img_pb-devops1_zst""",
                new[] {
                    @"2022-07-16-22-img_pb-devops1_zst\sda1\Recovery\Logs\Reload.xml",
                    @"2022-07-16-22-img_pb-devops1_zst\sda2\Program Files\PostgreSQL\13\share\information_schema.sql",
                    @"2022-07-16-22-img_pb-devops1_zst\sdb1\Kingsley\Prototype 1\Temp\Images\logo.jpg"
                });
        }
    }
}
