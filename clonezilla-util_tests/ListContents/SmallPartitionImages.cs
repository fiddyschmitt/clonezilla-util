namespace clonezilla_util_tests.ListContents
{
    [TestClass]
    [DoNotParallelize]
    public class SmallPartitionImages : ListTestBase
    {
        //runaway protection only: the slowest cold run so far took 26 s (2026-09-23)
        const int TimeoutMs = 15 * 60_000;

        [TestMethod, Timeout(TimeoutMs)]
        public void Bzip2()
        {
            TestUtility.ConfirmContainsStrings(
                Main.ExeUnderTest,
                """list --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda1.img.bz2""",
                [
                    @"2021-12-28_pb-devops1_sda1.img\partition0\Recovery\WindowsRE\ReAgent.xml",
                ]);
        }

        [TestMethod, Timeout(TimeoutMs)]
        public void gz()
        {
            TestUtility.ConfirmContainsStrings(
                Main.ExeUnderTest,
                """list --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda1.img.gz""",
                [
                    @"2021-12-28_pb-devops1_sda1.img\partition0\Recovery\WindowsRE\ReAgent.xml",
                ]);
        }

        [TestMethod, Timeout(TimeoutMs)]
        public void raw()
        {
            TestUtility.ConfirmContainsStrings(
                            Main.ExeUnderTest,
                            """list --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda1.img""",
                            [
                                @"2021-12-28_pb-devops1_sda1\partition0\Recovery\WindowsRE\ReAgent.xml",
                            ]);
        }

        [TestMethod, Timeout(TimeoutMs)]
        public void xz()
        {
            TestUtility.ConfirmContainsStrings(
                Main.ExeUnderTest,
                """list --input "E:\clonezilla-util-test resources\drive images\ddrescue backups (even includes deleted)\2021-12-28_pb-devops1_sda1.img.xz""",
                [
                    @"2021-12-28_pb-devops1_sda1.img\partition0\Recovery\WindowsRE\ReAgent.xml",
                ]);
        }

        [TestMethod, Timeout(TimeoutMs)]
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