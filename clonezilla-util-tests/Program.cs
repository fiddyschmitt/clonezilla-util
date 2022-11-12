﻿using clonezilla_util_tests.Tests;

var exeUnderTest = @"E:\Temp\clonezilla-util release\clonezilla-util.exe";
//exeUnderTest = @"E:\Temp\release\clonezilla-util.exe";

Console.WriteLine($"Testing: {exeUnderTest}");

var start = DateTime.Now;

MountTests.Test(exeUnderTest);
ListContentsTests.Test(exeUnderTest);
MountAsImageFilesTests.Test(exeUnderTest);
TrainTests.Test();

var duration = DateTime.Now - start;
Console.WriteLine($"Finished. Duration: {duration.TotalHours:N0} hours, {duration.Minutes} minutes.");