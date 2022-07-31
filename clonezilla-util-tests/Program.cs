using clonezilla_util_tests.Tests;

var exeUnderTest = @"C:\Users\fiddy\Desktop\dev\cs\ClonezillaApps\clonezilla-util\bin\Debug\net6.0\clonezilla-util.exe";

ListContentsTests.Test(exeUnderTest);
MountTests.Test(exeUnderTest);
MountAsImageFilesTests.Test(exeUnderTest);
TrainTests.Test();