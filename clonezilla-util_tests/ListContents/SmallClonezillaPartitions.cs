namespace clonezilla_util_tests.ListContents
{
    [TestClass]
    [DoNotParallelize]
    public class SmallClonezillaPartitions : ListTestBase
    {
        //runaway protection only: the slowest cold run so far took 3 s (2026-09-23)
        const int TimeoutMs = 15 * 60_000;

        [TestMethod, Timeout(TimeoutMs)]
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

        [TestMethod, Timeout(TimeoutMs)]
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

        [TestMethod, Timeout(TimeoutMs)]
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

        [TestMethod, Timeout(TimeoutMs)]
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