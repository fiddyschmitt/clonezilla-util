# Vendored: SharpZipLib inflater (v1.4.2)

Source: https://github.com/icsharpcode/SharpZipLib at tag `v1.4.2`, MIT licensed (per-file headers
retained). Files taken verbatim from `src/ICSharpCode.SharpZipLib/`:

- `Zip/Compression/Inflater.cs`, `InflaterDynHeader.cs`, `InflaterHuffmanTree.cs`
- `Zip/Compression/Streams/StreamManipulator.cs`, `OutputWindow.cs`
- `Checksum/Adler32.cs`, `IChecksum.cs`
- `Core/Exceptions/*.cs`

Local modifications (marked `[clonezilla-util addition]` in the code):

1. Namespaces rewritten `ICSharpCode.SharpZipLib` → `libGZip.Vendored.SharpZipLib` so a real
   SharpZipLib package reference elsewhere can never collide.
2. `StreamManipulator.PrimeBits(bitCount, value)` — zlib `inflatePrime()` equivalent.
3. `Inflater.PrimeForResume(window, bitCount, bitsValue)` — zlib `inflateSetDictionary()` (raw mode)
   + `inflatePrime()` in one call.

Why vendored at all: resuming DEFLATE mid-stream at a gztool/zran access point requires priming the
bit buffer and preloading the 32 KB window. No BCL or maintained NuGet inflater exposes both:
the BCL's DeflateStream exposes neither (and a bit-shifted input stream is NOT a valid substitute -
stored blocks re-align to the original byte grid), and ZLibDotNet 0.1.1 (which has the right API)
was found to mis-decode real DEFLATE streams (`invalid code -- missing end-of-block` mid-stream).
SharpZipLib's inflater is 20+ years battle-tested; the two additions above are small and guarded.
