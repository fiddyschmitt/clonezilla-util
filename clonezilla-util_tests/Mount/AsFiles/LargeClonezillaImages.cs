﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static clonezilla_util_tests.Mount.TestUtility;

namespace clonezilla_util_tests.Mount.AsFiles
{
    [TestClass]
    [DoNotParallelize]
    public class LargeClonezillaImages
    {
        [TestMethod]
        public void bzip2()
        {
            ConfirmFilesExist(
                Main.ExeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\clonezilla images\2022-07-16-22-img_pb-devops1_bzip2" -m L:\""",
                [
                    new FileDetails(@"L:\sda1\Recovery\Logs\Reload.xml", "f5a6df3c8f1ad69766afee3a25f7e376"),
                    new FileDetails(@"L:\sda2\Program Files\PostgreSQL\13\share\information_schema.sql", "97a4aa50fa682b00457810d010ff5852"),
                    new FileDetails(@"L:\sdb1\Kingsley\Prototype 1\Temp\Images\logo.jpg", "0217ff1926ec5f82e1a120676eff70c3"),
                ]);
        }

        [TestMethod]
        public void gz()
        {
            ConfirmFilesExist(
                Main.ExeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\clonezilla images\2022-07-17-16-img_pb-devops1_gz" -m L:\""",
                [
                    new FileDetails(@"L:\sda1\Recovery\Logs\Reload.xml", "f5a6df3c8f1ad69766afee3a25f7e376"),
                    new FileDetails(@"L:\sda2\Program Files\PostgreSQL\13\share\information_schema.sql", "97a4aa50fa682b00457810d010ff5852"),
                    new FileDetails(@"L:\sdb1\Kingsley\Prototype 1\Temp\Images\logo.jpg", "0217ff1926ec5f82e1a120676eff70c3"),
                ]);
        }

        [TestMethod]
        public void xz()
        {
            ConfirmFilesExist(
                Main.ExeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\clonezilla images\2022-07-17-12-img_pb-devops1_xz" -m L:\""",
                [
                    new FileDetails(@"L:\sda1\Recovery\Logs\Reload.xml", "f5a6df3c8f1ad69766afee3a25f7e376"),
                    new FileDetails(@"L:\sda2\Program Files\PostgreSQL\13\share\information_schema.sql", "97a4aa50fa682b00457810d010ff5852"),
                    new FileDetails(@"L:\sdb1\Kingsley\Prototype 1\Temp\Images\logo.jpg", "0217ff1926ec5f82e1a120676eff70c3"),
                ]);
        }

        [TestMethod]
        public void zst()
        {
            ConfirmFilesExist(
                Main.ExeUnderTest,
                """mount --input "E:\clonezilla-util-test resources\clonezilla images\2022-07-16-22-img_pb-devops1_zst" -m L:\""",
                [
                    new FileDetails(@"L:\sda1\Recovery\Logs\Reload.xml", "f5a6df3c8f1ad69766afee3a25f7e376"),
                    new FileDetails(@"L:\sda2\Program Files\PostgreSQL\13\share\information_schema.sql", "97a4aa50fa682b00457810d010ff5852"),
                    new FileDetails(@"L:\sdb1\Kingsley\Prototype 1\Temp\Images\logo.jpg", "0217ff1926ec5f82e1a120676eff70c3"),
                ]);
        }
    }
}
