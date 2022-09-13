using clonezilla_util_tests.Tests;

var exeUnderTest = @"C:\Users\fiddy\Desktop\dev\cs\ClonezillaApps\clonezilla-util\bin\Debug\net6.0\clonezilla-util.exe";
exeUnderTest = @"E:\Temp\clonezilla-util.v1.8.0.win-x64\clonezilla-util.exe";

Console.WriteLine($"Testing: {exeUnderTest}");

ListContentsTests.Test(exeUnderTest);
MountTests.Test(exeUnderTest);
MountAsImageFilesTests.Test(exeUnderTest);
TrainTests.Test();