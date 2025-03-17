namespace clonezilla_util_tests.ListContents
{
    [TestClass]
    [DoNotParallelize]
    public class SmallClonezillaPartitions
    {
        [TestMethod]
        public void Bzip2()
        {
            TestUtility.ConfirmContainsStrings(
                Main.ExeUnderTest,
                """list --input "E:\clonezilla-util-test resources\clonezilla images\2022-07-16-22-img_pb-devops1_bzip2" -p sda1 sdb1""",
                [
                    @"2022-07-16-22-img_pb-devops1_bzip2\sda1\Recovery\Logs\Reload.xml",
                    @"2022-07-16-22-img_pb-devops1_bzip2\sdb1\Kingsley\Prototype 1\Temp\Images\logo.jpg"
                ]);
        }

        [TestMethod]
        public void gz()
        {
            TestUtility.ConfirmContainsStrings(
                Main.ExeUnderTest,
                """list --input "E:\clonezilla-util-test resources\clonezilla images\2022-07-17-16-img_pb-devops1_gz" -p sda1 sdb1""",
                [
                    @"2022-07-17-16-img_pb-devops1_gz\sda1\Recovery\Logs\Reload.xml",
                    @"2022-07-17-16-img_pb-devops1_gz\sdb1\Kingsley\Prototype 1\Temp\Images\logo.jpg"
                ]);
        }

        [TestMethod]
        public void xz()
        {
            TestUtility.ConfirmContainsStrings(
                Main.ExeUnderTest,
                """list --input "E:\clonezilla-util-test resources\clonezilla images\2022-07-17-12-img_pb-devops1_xz" -p sda1 sdb1""",
                [
                    @"2022-07-17-12-img_pb-devops1_xz\sda1\Recovery\Logs\Reload.xml",
                    @"2022-07-17-12-img_pb-devops1_xz\sdb1\Kingsley\Prototype 1\Temp\Images\logo.jpg"
                ]);
        }

        [TestMethod]
        public void zst()
        {
            TestUtility.ConfirmContainsStrings(
                Main.ExeUnderTest,
                """list --input "E:\clonezilla-util-test resources\clonezilla images\2022-07-16-22-img_pb-devops1_zst" -p sda1 sdb1""",
                [
                    @"2022-07-16-22-img_pb-devops1_zst\sda1\Recovery\Logs\Reload.xml",
                    @"2022-07-16-22-img_pb-devops1_zst\sdb1\Kingsley\Prototype 1\Temp\Images\logo.jpg"
                ]);
        }
    }
}