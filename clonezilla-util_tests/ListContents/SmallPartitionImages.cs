namespace clonezilla_util_tests.ListContents
{
    [TestClass]
    [DoNotParallelize]
    public class SmallPartitionImages
    {
        [TestMethod]
        public void Bzip2()
        {
            TestUtility.ConfirmContainsStrings(
                Main.ExeUnderTest,
                """list --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda1.img.bz2""",
                [
                    @"2021-12-28_pb-devops1_sda1.img\partition0\Recovery\WindowsRE\ReAgent.xml",
                ]);
        }

        [TestMethod]
        public void gz()
        {
            TestUtility.ConfirmContainsStrings(
                Main.ExeUnderTest,
                """list --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda1.img.gz""",
                [
                    @"2021-12-28_pb-devops1_sda1.img\partition0\Recovery\WindowsRE\ReAgent.xml",
                ]);
        }

        [TestMethod]
        public void raw()
        {
            TestUtility.ConfirmContainsStrings(
                            Main.ExeUnderTest,
                            """list --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda1.img""",
                            [
                                @"2021-12-28_pb-devops1_sda1\partition0\Recovery\WindowsRE\ReAgent.xml",
                            ]);
        }

        [TestMethod]
        public void xz()
        {
            TestUtility.ConfirmContainsStrings(
                Main.ExeUnderTest,
                """list --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda1.img.xz""",
                [
                    @"2021-12-28_pb-devops1_sda1.img\partition0\Recovery\WindowsRE\ReAgent.xml",
                ]);
        }

        [TestMethod]
        public void zst()
        {
            TestUtility.ConfirmContainsStrings(
                            Main.ExeUnderTest,
                            """list --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda1.img.zst""",
                            [
                                @"2021-12-28_pb-devops1_sda1.img\partition0\Recovery\WindowsRE\ReAgent.xml",
                            ]);
        }
    }
}