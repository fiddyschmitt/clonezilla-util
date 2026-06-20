# Builds lib7zNative.dll with MSVC (x64). Run from anywhere.
# Requires Visual Studio 2026 (uses its vcvars64.bat).
$ErrorActionPreference = "Stop"

$vcvars = "C:\Program Files\Microsoft Visual Studio\18\Enterprise\VC\Auxiliary\Build\vcvars64.bat"
$root = "C:\Users\Smith\Desktop\dev\cs\clonezilla-util\lib7zNative"
$v = "$root\vendor\7zip"
$build = "$root\build"
$obj = "$build\obj"
New-Item -ItemType Directory -Force $obj | Out-Null

$sources = @(
  "$root\src\shim.cpp",
  "$v\C\Alloc.c","$v\C\CpuArch.c","$v\C\Sort.c","$v\C\7zCrc.c","$v\C\7zCrcOpt.c","$v\C\Threads.c",
  "$v\CPP\Common\IntToString.cpp","$v\CPP\Common\MyString.cpp","$v\CPP\Common\StringConvert.cpp","$v\CPP\Common\StringToInt.cpp","$v\CPP\Common\UTFConvert.cpp","$v\CPP\Common\MyVector.cpp","$v\CPP\Common\NewHandler.cpp","$v\CPP\Common\Wildcard.cpp","$v\CPP\Common\CommandLineParser.cpp","$v\CPP\Common\DynLimBuf.cpp","$v\CPP\Common\ListFileUtils.cpp",
  "$v\CPP\Windows\DLL.cpp","$v\CPP\Windows\FileDir.cpp","$v\CPP\Windows\FileFind.cpp","$v\CPP\Windows\FileIO.cpp","$v\CPP\Windows\FileName.cpp","$v\CPP\Windows\PropVariant.cpp","$v\CPP\Windows\PropVariantConv.cpp","$v\CPP\Windows\System.cpp","$v\CPP\Windows\TimeUtils.cpp","$v\CPP\Windows\ErrorMsg.cpp","$v\CPP\Windows\FileLink.cpp","$v\CPP\Windows\Synchronization.cpp","$v\CPP\Windows\Registry.cpp",
  "$v\CPP\7zip\Common\CreateCoder.cpp","$v\CPP\7zip\Common\CWrappers.cpp","$v\CPP\7zip\Common\FileStreams.cpp","$v\CPP\7zip\Common\InBuffer.cpp","$v\CPP\7zip\Common\OutBuffer.cpp","$v\CPP\7zip\Common\ProgressUtils.cpp","$v\CPP\7zip\Common\PropId.cpp","$v\CPP\7zip\Common\StreamObjects.cpp","$v\CPP\7zip\Common\StreamUtils.cpp","$v\CPP\7zip\Common\FilterCoder.cpp","$v\CPP\7zip\Common\LimitedStreams.cpp","$v\CPP\7zip\Common\MethodId.cpp","$v\CPP\7zip\Common\MethodProps.cpp","$v\CPP\7zip\Common\OffsetStream.cpp","$v\CPP\7zip\Common\UniqBlocks.cpp","$v\CPP\7zip\Common\InOutTempBuffer.cpp","$v\CPP\7zip\Common\MultiOutStream.cpp","$v\CPP\7zip\Common\FilePathAutoRename.cpp",
  "$v\CPP\7zip\Compress\CopyCoder.cpp",
  "$v\CPP\7zip\Archive\Common\ItemNameUtils.cpp",
  "$v\CPP\7zip\UI\Common\OpenArchive.cpp","$v\CPP\7zip\UI\Common\LoadCodecs.cpp","$v\CPP\7zip\UI\Common\ArchiveOpenCallback.cpp","$v\CPP\7zip\UI\Common\ArchiveExtractCallback.cpp","$v\CPP\7zip\UI\Common\SetProperties.cpp","$v\CPP\7zip\UI\Common\DefaultName.cpp","$v\CPP\7zip\UI\Common\ExtractingFilePath.cpp","$v\CPP\7zip\UI\Common\PropIDUtils.cpp","$v\CPP\7zip\UI\Common\SortUtils.cpp"
)

$rsp = "$build\cl.rsp"
$lines = @(
  "/nologo /O2 /MD /EHsc /std:c++17",
  "/DZ7_EXTERNAL_CODECS /D_UNICODE /DUNICODE /DWIN32 /DNDEBUG",
  "/I`"$v\CPP`"",
  "/Fo`"$obj\\`"",
  "/LD"
)
$lines += ($sources | ForEach-Object { "`"$_`"" })
$lines += "/Fe`"$build\lib7zNative.dll`""
$lines += "/link oleaut32.lib ole32.lib user32.lib advapi32.lib shell32.lib"
Set-Content -Path $rsp -Value $lines -Encoding ascii

cmd /c "`"$vcvars`" >nul 2>&1 && cl @`"$rsp`"" 2>&1
Write-Output "=== EXIT: $LASTEXITCODE ==="
if (Test-Path "$build\lib7zNative.dll") { Write-Output ("DLL built: " + (Get-Item "$build\lib7zNative.dll").Length + " bytes") } else { Write-Output "DLL NOT produced" }
