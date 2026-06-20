# Vendored 7-Zip source

- **Source:** https://github.com/ip7z/7zip (shallow clone of the default branch)
- **Version:** 26.01 (see `vendor/7zip/C/7zVersion.h`)
- **Vendored:** 2026-06-19

## What this is

`lib7zNative` wraps 7-Zip's own `UI/Common` open/extract orchestration
(`OpenArchive.cpp` / `CArchiveLink` + `LoadCodecs.cpp` / `CCodecs`) behind the C ABI in
`include/lib7znative.h`. This is the layer that gives 7zFM its **automatic format detection** and
recursive **"open inside"** - it is NOT part of `7z.dll`.

The actual format handlers + codecs are loaded at runtime from the **bundled `7z.dll`**
(`ext/7-Zip/win-x64/7z.dll`); they are not vendored or statically linked here.

## Scope: recursive "open inside" is intentionally NOT exposed

`CArchiveLink` is capable of recursively opening a nested archive (e.g. a `.zip` stored *inside* a
partition) as a sub-tree. We deliberately do **not** surface that in the VFS, and this is **not a
regression**: the previous 7zFM-based path used 7zFM only as an extraction engine and got its file
list from `7z.exe l`, which also lists a single archive level - a nested archive showed up as a plain
*file*, never an expanded folder. The native engine matches that exactly: `SevenZip_GetItem*`
enumerate one filesystem level, so we are at parity with the old behaviour.

True open-inside (nested archive files appearing as browsable folders) would be a *new* feature: it
needs nested path/index addressing in the C ABI, extract-then-reopen for filesystem-handler children
(no `IInArchiveGetStream`), and lazy per-navigation expansion in the Dokan VFS. It's a cleanly
separable follow-up if ever wanted - the current engine is the right foundation for it.

## Re-snapshotting a newer 7-Zip release

1. Re-clone the desired tag into `vendor/7zip` (shallow is fine); delete the nested `.git`.
2. Rebuild - `CMakeLists.txt` lists exactly which 7-Zip `.cpp` files are compiled. If 7-Zip moved /
   renamed files or added dependencies, adjust the source list until it links.
3. Update the Version/date above.
4. Re-run `clonezilla-util_tests` (ground-truth MD5s) before shipping.

## Licensing

7-Zip is GNU LGPL (+ "unRAR license restriction" for RAR code, + BSD for some code). We **exclude the
RAR handler**, so the unRAR restriction does not apply. Because 7-Zip source is compiled into
`lib7zNative.dll`, that DLL is an LGPL derivative: ship 7-Zip's `License.txt` + notices alongside it,
keep its source available, and keep `7z.dll` as an unmodified LGPL dynamic dependency. See
`vendor/7zip/DOC/License.txt`.

## Note

Only the files referenced by `CMakeLists.txt` are compiled; the rest of the clone is unused and may be
pruned. Keeping the full tree makes re-snapshotting trivial.
