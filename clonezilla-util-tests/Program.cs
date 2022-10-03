using clonezilla_util_tests.Tests;

var exeUnderTest = @"C:\Users\fiddy\Desktop\dev\cs\ClonezillaApps\clonezilla-util\bin\Debug\net6.0\clonezilla-util.exe";
exeUnderTest = @"E:\Temp\release\clonezilla-util.exe";

Console.WriteLine($"Testing: {exeUnderTest}");

MountTests.Test(exeUnderTest);
ListContentsTests.Test(exeUnderTest);
MountAsImageFilesTests.Test(exeUnderTest);
TrainTests.Test();