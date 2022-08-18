using clonezilla_util_tests.Tests;

var exeUnderTest = @"C:\Users\fiddy\Desktop\dev\cs\ClonezillaApps\clonezilla-util\bin\Debug\net6.0\clonezilla-util.exe";
//exeUnderTest = @"C:\Users\fiddy\Desktop\Temp\2022-07-31\clonezilla-util.v1.6.0.win-x64\clonezilla-util.exe";
//exeUnderTest = @"C:\Users\fiddy\Desktop\Temp\2022-07-31\clonezilla-util.v1.6.0.win-x86\clonezilla-util.exe";

ListContentsTests.Test(exeUnderTest);
MountTests.Test(exeUnderTest);
MountAsImageFilesTests.Test(exeUnderTest);
TrainTests.Test();