# Test-by-test performance analysis

Profiling campaign over every test in the suite (71 as of 2026-07-21), in run order.
Method: run the test's exe command against the warm deployed cache under `dotnet-trace`
(CPU sampling), aggregate hot stacks, decide whether anything is worth optimising, record the
verdict. Durations from the 2026-07-21 suite pair (cold 0700 / warm 1700), machine-dependent —
the environmental swing is documented in PERFORMANCE_PLAN.md.

| # | Test | Cold | Warm | Analysed | Verdict |
|---|------|------|------|----------|---------|
| 1 | ListContents.LargeClonezillaPartitions.Bzip2 | 17.2 min | 3.2 min | 2026-07-21 | **FIXED: SharedStream gate contention** — warm 150→109 s (−27%) |
| 2 | ListContents.LargeClonezillaPartitions.Gz | 10.9 min | 1.1 min | 2026-07-21 | **FIXED ×3**: serving-decision cache; STJ file lists (also killed the cold GC storm). Warm 40→20 s; listing phase → 18 s |
| 3 | ListContents.LargeClonezillaPartitions.Xz | 27 min | 3.1 min | 2026-07-21 | **FIXED (L4)**: listing skips extractor opens on cache hit — warm 102→14 s (7×) |
| 4 | ListContents.LargeClonezillaPartitions.Zst | 5.2 min | 1.3 min | 2026-07-21 | **Clean — no action.** Cold 304 s IO-bound (59.5% raw file reads); warm 78→7 s (11×) from the accumulated #1–#3 fixes, no zst-specific work needed |
| 5 | ListContents.LargeDriveImages.Bzip2 | 14.5 min | 2.2 min | 2026-07-21 | Cold clean (Release 900 s ≈ suite 870 s; Debug-config artifact explained the scare). **FIXED (L6)**: drive-image partitions get real caches — warm 75→21 s (3.6×) |
| 6 | ListContents.LargeDriveImages.Gz | 58.3 min | 1 min | 2026-07-24 | **Clean — no action.** Cold 68.3 min = gzip index build (66.4 min, within machine variance of the suite's good-night 58.3). Warm 60→18 s from L6, no gz-specific work |
| 7 | ListContents.LargeDriveImages.Raw | 41.2 sec | 39.3 sec | 2026-07-24 | **FIXED (L6, raw path)**: warm 39→12 s (3.3×); pre-L6 this test had no caching at all. Cold 48 s = one-off population |
| 8 | ListContents.LargeDriveImages.Xz | 3.9 min | 4 min | 2026-07-24 | **FIXED (L6): warm 240→17 s (14×)** — the biggest L6 win; pre-L6 warm re-listed NTFS through xz 32 MiB spans every open. Cold ≈ suite |
| 9 | ListContents.LargeDriveImages.Zst | 15.5 min | 1.2 min | 2026-07-24 | **FIXED (L6)**: warm 72→15 s at the floor (41 s first-warm = paging the 879 MB `.zsi` back in). Cold = index build, ≈ suite + variance |
| 10 | ListContents.Partclone.MixedPartcloneFormats | 4.5 sec | 3.1 sec | 2026-07-24 | **Clean** — 2 s / 1 s measured; trivial |
| 11 | ListContents.SmallClonezillaPartitions.Bzip2 | 34.2 sec | 33 sec | 2026-07-24 | **Clean** — warm 33→3 s (11×) from the #1–#4 fix set; cold = honest 2-partition index build |
| 12 | ListContents.SmallClonezillaPartitions.gz | 10.5 sec | 5.8 sec | 2026-07-24 | **Clean** — 8 s / 1 s |
| 13 | ListContents.SmallClonezillaPartitions.xz | 27.9 sec | 25.9 sec | 2026-07-24 | **Clean** — warm 26→3 s (L4 at small scale); cold ≈ suite |
| 14 | ListContents.SmallClonezillaPartitions.zst | 10.4 sec | 7.5 sec | 2026-07-24 | **Clean** — 8 s / 1 s |
| 15 | ListContents.SmallPartitionImages.Bzip2 | 43.2 sec | 32.3 sec | 2026-07-24 | **FIXED (L6)**: warm 32→11 s; residual = 50 MB identity hash through bzip2 |
| 16 | ListContents.SmallPartitionImages.gz | 15.9 sec | 15.8 sec | 2026-07-24 | **FIXED (L6)**: warm 16→2 s |
| 17 | ListContents.SmallPartitionImages.raw | 4.5 sec | 6.2 sec | 2026-07-24 | **FIXED (L6)**: warm 6→1 s |
| 18 | ListContents.SmallPartitionImages.xz | 48.3 sec | 24.7 sec | 2026-07-24 | **FIXED (L6)**: warm 25→4 s |
| 19 | ListContents.SmallPartitionImages.zst | 8.1 sec | 8.5 sec | 2026-07-24 | **FIXED (L6)**: warm 8.5→2 s |
| 20 | Mount.AsFiles.Ext4.ext4 | 15.4 sec | 14.1 sec | 2026-07-24 | **Clean** — warm 14→4 s (L6 tree-from-cache); cold ≈ suite |
| 21 | Mount.AsFiles.Ext4.ext4_zst | 54.3 sec | 54.5 sec | 2026-07-24 | **FIXED (L6+L7+L9+L8)**: warm 54.5→5 s after probe removal — the zstd fill-span index makes the ext4 scan nearly free; one-off 24 s index build |
| 22 | Mount.AsFiles.LargeClonezillaImages.bzip2 | 8.4 min | 5.2 min | 2026-07-24 | **FIXED (L9)**: parallel pool opens — warm 267→84 s (3.2×); sda2 pool 226→57 s |
| 23 | Mount.AsFiles.LargeClonezillaImages.gz | 1.1 min | 1.1 min | 2026-07-24 | **Clean** — warm 66→26 s; pool opens through gz cost ~2 s/worker vs bzip2's ~17 (L9 is decode-bound). Cold 407 s = true index build |
| 24 | Mount.AsFiles.LargeClonezillaImages.xz | 6.9 min | 7.1 min | 2026-07-24 | **FIXED (L9)**: warm 309→110 s (2.8×); sda2 pool ~280→87 s. Cold 1458 s = true LZMA2 checkpoint build |
| 25 | Mount.AsFiles.LargeClonezillaImages.zst | 1.6 min | 1.7 min | 2026-07-24 | Warm 102→53 s — L9 middling case (~45 s pool opens, ~4 s/worker). L9 evidence complete: xz 280 / bz2 202 / zst 45 / gz 8 s |
| 26 | Mount.AsFiles.LargeDriveImages.bzip2 | 2.3 min | 2.4 min | 2026-07-24 | Warm 144→126 s — L9 residual (pool opens through bzip2). MD5s verified |
| 27 | Mount.AsFiles.LargeDriveImages.gz | 1 min | 1.1 min | 2026-07-24 | **Clean** — warm 66→26 s, near floor |
| 28 | Mount.AsFiles.LargeDriveImages.Raw | 39.4 sec | 37.9 sec | 2026-07-24 | **Clean** — warm 38→18 s (L6 raw path + tree build for 830k files) |
| 29 | Mount.AsFiles.LargeDriveImages.xz | 4 min | 4.5 min | 2026-07-24 | Warm 270→124 s — L9 residual through 32 MiB spans |
| 30 | Mount.AsFiles.LargeDriveImages.zst | 1.3 min | 1.3 min | 2026-07-24 | **Clean** — warm 78→29 s, near floor |
| 31 | Mount.AsFiles.LuksClonezillaImages.luks_ext4_500GB_gz | 34 sec | 29.9 sec | 2026-07-24 | Warm 30→25 s; diagnosed: 23 s = L9 pool opens through LUKS+gz (5-file list). No new lead |
| 32 | Mount.AsFiles.LuksClonezillaImages.luks_ext4_500GB_zst | 21 sec | 21 sec | 2026-07-24 | Warm 21→15 s — same L9 shape via zst |
| 33 | Mount.AsFiles.LuksClonezillaImages.luks_ntfs_20GB | 3.1 sec | 2.2 sec | 2026-07-24 | **Clean** — 1 s / 1 s |
| 34 | Mount.AsFiles.LuksClonezillaImages.luks_ntfs_6GB | 2.2 sec | 3.2 sec | 2026-07-24 | **Clean** — 2 s / 1 s |
| 35 | Mount.AsFiles.LuksParcloneImages.luks_ext4_500GB_gz | 3 min | 2.6 min | 2026-07-25 | **FIXED (L10+L7+L8)**: warm 139→91 s (probe removal: pool scan via gz-zran index instead of restarts). Residual = honest ext4 metadata decode through LUKS+partclone+gz |
| 36 | Mount.AsFiles.LuksParcloneImages.luks_ext4_500GB_zst | 3.1 min | 2.8 min | 2026-07-25 | Warm 168→124 s — same L10 shape via zst |
| 37 | Mount.AsFiles.LuksParcloneImages.luks_ntfs_20GB | 12.2 sec | 12.2 sec | 2026-07-25 | **Clean** — warm 12→1 s |
| 38 | Mount.AsFiles.LuksParcloneImages.luks_ntfs_6GB | 10.2 sec | 35.9 sec | 2026-07-25 | **Clean** — 10/3 s (suite's 35.9 warm was an environmental outlier) |
| 39 | Mount.AsFiles.Misc.LastestClonezilla_2022_06_29 | 3.1 sec | 3.2 sec | 2026-07-25 | **Clean** — 1 s / 1 s |
| 40 | Mount.AsFiles.Misc.MultipleContainers_MultiplePartitions | 40 sec | 15.4 sec | 2026-07-25 | **Clean** — 8/3 s; mixed gz clonezilla + zst partition image, all fixes compound |
| 41 | Mount.AsFiles.Partclone.dd | 112.4 min | 1.7 min | 2026-07-25 | **Clean** — cold 88.8 min beat the suite's best-ever; warm 102→35 s. MD5s verified |
| 42 | Mount.AsFiles.Partclone.gz | 1.4 min | 18.7 sec | 2026-07-25 | **Clean** — 12/3 s; bare-partclone at small scale benefits fully |
| 43 | Mount.AsFiles.Partclone.MixedPartcloneFormats | 7 sec | 2.2 sec | 2026-07-25 | **Clean** — 2/1 s |
| 44 | Mount.AsFiles.Partclone.PartcloneImage | 6.2 sec | 4.2 sec | 2026-07-25 | **Clean** — 2/1 s |
| 45 | Mount.AsFiles.SmallClonezillaPartitions.Bzip2 | 37.2 sec | 35.2 sec | 2026-07-25 | Warm 35→12 s; residual = small-scale L9 through bzip2 |
| 46 | Mount.AsFiles.SmallClonezillaPartitions.gz | 10 sec | 3.2 sec | 2026-07-25 | **Clean** — 5/2 s |
| 47 | Mount.AsFiles.SmallClonezillaPartitions.LZ4 | 4.2 sec | 3.1 sec | 2026-07-25 | **Clean** — 2/1 s |
| 48 | Mount.AsFiles.SmallClonezillaPartitions.LZIP | 5.2 sec | 3.2 sec | 2026-07-25 | **Clean** — 1/1 s |
| 49 | Mount.AsFiles.SmallClonezillaPartitions.Uncompressed | 2.1 sec | 2.1 sec | 2026-07-25 | **Clean** — 1/1 s |
| 50 | Mount.AsFiles.SmallClonezillaPartitions.xz | 29 sec | 24 sec | 2026-07-25 | Warm 24→8 s; residual = small-scale L9 through xz |
| 51 | Mount.AsFiles.SmallClonezillaPartitions.zst | 8.6 sec | 8.3 sec | 2026-07-25 | **Clean** — 4/1 s |
| 52 | Mount.AsFiles.SmallPartitionImages.Bzip2 | 32.6 sec | 33.6 sec | 2026-07-25 | **FIXED (L6)**: warm 34→9 s |
| 53 | Mount.AsFiles.SmallPartitionImages.gz | 37.4 sec | 38.3 sec | 2026-07-25 | **FIXED (L6)**: warm 38→3 s — starkest small-image before/after |
| 54 | Mount.AsFiles.SmallPartitionImages.Raw | 6.2 sec | 8.2 sec | 2026-07-25 | **Clean** — 3/1 s |
| 55 | Mount.AsFiles.SmallPartitionImages.xz | 51.2 sec | 26.4 sec | 2026-07-25 | Warm 26→14 s (L6 + xz L9 residual) |
| 56 | Mount.AsFiles.SmallPartitionImages.zst | 31.1 sec | 4.2 sec | 2026-07-25 | **Clean** — 5/2 s |
| 57 | Mount.AsFiles.UbuntuFileSystems.ext4 | 4.7 min | 2 min | 2026-07-25 | Warm 120→86 s; residual = eager ext4 extractor scans (L9 family) |
| 58 | Mount.AsFiles.UbuntuFileSystems.ext4_lvm | 5.2 min | 2.3 min | 2026-07-25 | Warm 138→91 s — same shape through LVM |
| 59 | Mount.AsImageFiles.ImageFileTests.Gz | 19.2 sec | 14.5 sec | 2026-07-25 | **Clean** — 12/10 s; serving-dominated (whole .img copied through gz) |
| 60 | Mount.AsImageFiles.ImageFileTests.LuksNtfs6GB | 51 sec | 51.1 sec | 2026-07-25 | **Clean** — 45/40 s; honest LUKS+zst decode of the whole partition image |
| 61 | Mount.AsImageFiles.ImageFileTests.Partclone | 9.9 sec | 10.8 sec | 2026-07-25 | **Clean** — 8/7 s (1 GiB partial hash, per test definition) |
| 62 | Mount.AsImageFiles.ImageFileTests.UncompressedPartitionImage_and_gzClonezillaImage | 16.9 sec | 17.3 sec | 2026-07-25 | **Clean** — 16/12 s |
| 63 | Mount.AsImageFiles.ImageFileTests.Zst | 11.9 sec | 6.1 sec | 2026-07-25 | **Clean** — 10/6 s |
| 64 | Partclone.PartcloneContentMapTests.EdgePatterns | 813 ms | 856 ms | 2026-07-25 | **Trivial** — in-process unit tests; all 7 pass in 3 s total, nothing to optimize |
| 65 | Partclone.PartcloneContentMapTests.V1_Typical | 78 ms | 35 ms | 2026-07-25 | **Trivial** (see #64) |
| 66 | Partclone.PartcloneContentMapTests.V2_DeviceLargerThanBitmap | 1.2 sec | 940 ms | 2026-07-25 | **Trivial** (see #64) |
| 67 | Partclone.PartcloneContentMapTests.V2_LargeBlocksPerChecksum_RunSpansManyStrips | 54 ms | 38 ms | 2026-07-25 | **Trivial** (see #64) |
| 68 | Partclone.PartcloneContentMapTests.V2_NoChecksum_Typical | 38 ms | 30 ms | 2026-07-25 | **Trivial** (see #64) |
| 69 | Partclone.PartcloneContentMapTests.V2_PartialLastBlock | 32 ms | 28 ms | 2026-07-25 | **Trivial** (see #64) |
| 70 | Partclone.PartcloneContentMapTests.V2_WithChecksum_Typical | 39 ms | 39 ms | 2026-07-25 | **Trivial** (see #64) |
| 71 | Sparse.SparseTests.ExtractAndSparsifyFile | 13.6 min | 23.1 min | 2026-07-25 | **Clean** — 4.5 min both legs (ratio 0.02); identical cold/warm proves the suite's 23.1 warm was environmental. Sequential decode + sparse write; nothing cacheable |

## Campaign complete (2026-07-25)

All 71 tests analysed, cold and warm, with per-test verdicts above. Every measured leg's output
was verified (listing hashes / file MD5s through the mounts).

**Fixes shipped during the sweep** (each profile-verified, all committed):
1. SharedStream length caching (#1) — warm bzip2 −27%.
2. Serving-decision persistence (#2) — −10 s per partition per open.
3. STJ file lists (#2) — −25 s per large partition, killed the cold GC storm.
4. L4: cache-first listing, no extractor on hit (#3) — warm xz listing 9×.
5. L6: drive-image partitions get real caches via the whole-file identity folder (#5) —
   warm drive listings 3–14×, and every bare-image mount benefits.

**Leads pending approval:**
- **L7** (#21, designed): persist the uncompressed length in the identity folder — kills 24 s per
  open of sequential-decision compressed images (ext4_zst's full-33 GB decode-for-length).
- **L9** (#22, diagnosed): eager native-7z pool opens dominate warm mounts of large compressed
  images (xz ~280 s, bzip2 ~202 s, zst ~45 s, gz ~8 s; ext4/LVM extractors same family). Fix
  design (serialize first open to warm CachingStream, or background pool growth) needs
  NativeExtractorPool + CODE_REVIEW_PLAN DokanVFS reading first.
- **L10** (#35, diagnosed): bare-partclone mounts rebuild the partclone layer (41 s, map not
  cached) and pay L9 through a restart-gz stream (94 s); the same image via the clonezilla flow
  mounts in 25 s.
- **L8** (parked): serving-decision heuristic ignores random access for huge-decompressed images.

**Protocol notes for interpreting the table:** suite "cold" numbers for Mount tests of large
images are index-warm (earlier ListContents tests build the indexes); private-cache colds here
are true from-scratch builds. Absolute numbers carry the documented machine variance; every
before/after claim used the same config and harness both sides. Tests #1–#4 were profiled on the
Debug build (see methodology note below); #5 onward used Release.

## Findings

**Methodology note (2026-07-21, discovered during #5):** tests 1-4 were profiled against the
DEBUG build. Before/after improvements (same config both sides) stand, and package-code-dominated
results (Xz/Zstd cold ≈ suite) are unaffected because NuGet package code is Release regardless -
but absolute comparisons to suite numbers skew wherever REPO code dominates (bzip2's
BZip2BlockFinder). From #5 onward the campaign profiles the Release build.

### 1. ListContents.LargeClonezillaPartitions.Bzip2  (warm, 150 s run, dotnet-trace cpu+thread-time)

Sample distribution (thread-time sampling: blocked threads are counted, so idle-wait rows are
pool/IOCP threads parking, not waste):
- **Monitor.Enter_Slowpath 18.3% exclusive — REAL contention.** The Batch 8 parallel group decode
  (`Bzip2StreamSeekable.Read` → `Parallel.For` → `DecodeBlock`, 37.9% inclusive) serialises on the
  `SharedStream` gate. Root cause found: `SharedStreamView.get_Length` is 15.65% inclusive —
  `DecodeBlock` calls `BlockBitRange(block, sourceView.Length)` (`Bzip2StreamSeekable.cs:215`)
  once per block, and every `get_Length` takes the shared lock AND does a `FileStream.Length`
  syscall (`SharedStream.cs:43-46`). ~35-40 blocks per 32 MB group × 12 workers = a storm of
  gate acquisitions colliding with the legitimate data reads.
- Genuine bzip2 decode (SharpCompress `CBZip2InputStream.*`): ~16% — the actual work.
- `GC.RunFinalizers` 5.4% — finalizer churn in the per-block path (secondary; worth a gcdump later).
- File IO reads 3.3%; the rest is parked threads.

**Fix applied (2026-07-21):** `SharedStream` caches the base stream's length (lazy, lock-free
reads; sources are read-only so it is invariant), and `Bzip2StreamSeekable` hoists the compressed
length to a ctor-time field so `DecodeBlock` never touches the view's Length.

**Retest, identical warm workload: 150 s → 109 s (−27%).** `Monitor.Enter_Slowpath` disappeared
from the top-10 entirely (was 18.3% exclusive); the busy threads now spend their samples in the
actual SharpCompress decode (`GetAndMoveToFrontDecode` 16.4% excl, up from 9.7% — workers decode
instead of contending). `GC.RunFinalizers` also left the top-10. Expect the other warm bzip2-heavy
tests (#5, #11, #15, #22, #26, #45, #52) to benefit on the next suite run.
New secondary observation: `Utilities.GetMemoryPressure()` at 2.8% exclusive — small, but a
polling cost worth a look when we profile a mount test.

### 2. ListContents.LargeClonezillaPartitions.Gz  (cold 619 s + warm 40 s, private cache)

**Cold (619 s; suite 654):** the expected work is there — file IO reads 20.8% excl, vendored
`Inflater.DecodeHuffman` 15%, memmove 7.3%, `Crc32.Update` 3.1% (slicing-by-8 visible) — but the
single biggest consumer is **GC infrastructure: ArrayPool TLS-bucket trimming 28.7% exclusive +
`GC.RunFinalizers` 19.8% exclusive (48.5% inclusive)**. Absent from the warm leg, so it belongs to
the cold-only phases: the REAL 7z listing of 721k files (warm reads Files.json instead) and/or the
index build's allocation pattern. **Lead L1 (potentially large, affects EVERY cold listing test):
run an allocation trace / gcdump of the cold listing phase to find what mass-produces finalizable
objects and pool churn.** Suspects: per-entry objects in the 7z list parse, per-window
DeflateStream/SafeHandle churn in the index build.

**Warm (40 s; suite 66):** ~20 s is the two 10-second seekable-vs-sequential perf tests (fixed
cost per partition per open); most of the rest is **Newtonsoft deserialization of the 278 MB
Files.json** — `String/span.Trim` alone is 25% of samples (721k ArchiveEntry parses; DateTime
parsing trims internally), plus the JSON's own file IO. Leads:
- **L2: persist the perf-test verdict** per stream in the cache — saves a fixed 10 s per
  partition on every warm open, across the entire suite (dozens of tests).
- **L3: replace the Files.json format** (System.Text.Json source-gen, or a compact binary list) —
  the 278 MB parse costs ~20-25 s per 721k-file partition on every open, warm or cold.

**All three leads RESOLVED (2026-07-21):**
- L2: `{name}.serving_decision.txt` in the partition cache (whole-file cache folder for drive
  images); a cache clear re-evaluates. Second-open probes eliminated.
- L3: file lists read/written with System.Text.Json directly on the UTF-8 stream (IncludeFields,
  parameterless [JsonConstructor] on ArchiveEntry). Old Newtonsoft-written caches load unchanged
  (verified on the real 278 MB file, identical 722,134-line listing); new files are compact
  (237 MB vs 278) and write without materialising a 556 MB string.
- L1: resolved BY L3 — the re-listing phase re-profiled clean (GC.RunFinalizers 48.5%→4.5% incl,
  ArrayPool trimming gone from top-12) and collapsed to **18 s**. The storm was the old indented
  Newtonsoft serialize churning the LOH on every cold listing.
- Net test #2 warm: 40 s → **20 s** (suite had 66 s); remaining floor ≈ STJ parse of 237 MB
  (~10 s) + IO/print. A binary list format could cut further - diminishing returns, revisit only
  if list-load shows up again.

### 3. ListContents.LargeClonezillaPartitions.Xz  (cold 1621 s + warm 102 s, private cache)

**Cold (1621 s; suite 1620):** honest LZMA2 work — `XzSeekable...Decoder.Code` 32% exclusive; the
single-block checkpoint build is inherently serial. No fixable decode-side finding.

**Warm (102 s; suite 186, already halved by L2+L3): ~100 s is `SevenZipNativeArchive..ctor` via
`NativeExtractorPool`** — the pool opens ALL native-7z workers up front
(`MountedPartitionImage.cs:88`) BEFORE the file-list cache is even consulted (`:126`), and each
open makes 7z scan NTFS structures through the compressed stream. With xz's 32 MiB spans every
scattered read decodes ~16 MiB average from a checkpoint — hence xz warm (102 s) ≫ gz warm (20 s).
A pure listing never reads content, so on a warm cache this work buys nothing. The eager open is
deliberate (D3-era: never open lazily inside a Dokan callback) - the fix must respect that.
Corollaries: the `DynamicResolver+DestroyScout.Finalize` storm (30% of warm samples) and the
residual cold finalizer/Trim entries live under these native opens (COM marshaling churn), and
test #1's warm decode was largely the same open scans - fixing L4 improves the bzip2/zst/gz
listings too.

**Lead L4 (big; all 19 warm ListContents tests + faster mounts-with-cache):** construct the
extractor lazily (`Lazy<IExtractor>` wired into the FileEntry factories); short-circuit the file
list from cache BEFORE touching it; mount flows force the Lazy during mount population so nothing
opens inside a Dokan callback (invariant preserved). Expected: warm xz listing 102 → ~15 s.

**Considered and rejected: denser xz spans (L5).** 32 MiB spans make cold random reads expensive,
but sda2's `.xzi` is already 2.6 GB (inline windows); halving the span doubles the index. L4
removes the listing-path pain; mounts pay the decode only on first real content access.

**L4 RESOLVED (2026-07-21), scoped to the listing flow only** (the mount flow's eager-open Dokan
invariant is untouched): `Program.ListContents` now consults the cached file list BEFORE
constructing any extractor; only a cache miss opens one (and disposes it after enumerating).
Retest: warm xz listing **102 s → 14 s** (9 s page-warm), identical 722,134 lines; cache-miss
branch verified (deleted one partition's list → re-listed via extractor, cache regenerated).
Every warm ListContents test benefits; the DestroyScout/COM-churn storm goes with it.

### 4. ListContents.LargeClonezillaPartitions.Zst  (cold 304 s + warm 7 s, private cache)

First clean sweep — no new findings; the #1–#3 fix set fully generalizes.

**Cold (304 s; suite 312): IO-bound, no action.** 59.5% of samples are raw file reads — the build
ingests 19.9 GB from E: faster than zstd can be made to matter (ZstdSharp decode only ~12%,
window re-compression 2%). The disk is the ceiling, not the code.

**Warm (7 s; suite 78 — 11×):** the profile is console output (`StreamWriter.Flush` 23%,
printing 722k lines), waits, and GC polling. Effectively optimal. Nothing zst-specific was
touched: the decision cache, STJ lists and lazy extractor are format-agnostic.

### 5. ListContents.LargeDriveImages.Bzip2  (Release cold 900 s + warm 75 s, private cache)

The test that exposed the Debug/Release methodology error. Sequence of evidence:
- Debug traced cold: 1630 s (43% `SemaphoreSlim.WaitCore` — Batch 8c pipeline consumers starved).
- Debug untraced cold: 1472 s (so trace overhead ~10%, not the cause).
- **Release untraced cold: 900 s ≈ suite 870 s.** The 2× was the Debug build: the pipeline's
  producer (`BZip2BlockFinder`, repo code) runs unoptimized in Debug, while consumers (SharpCompress
  decode, NuGet = always Release) idle behind it. Yesterday's SharedStream/Bzip2StreamSeekable
  commit (0187f88) is exonerated; cold is honest work — no action.

**Warm (75 s):** unlike the partition tests, warm drive-image listing still spends its time in
real bzip2 decode + NTFS scan. Root cause: **drive-image partitions are constructed with a null
`PartitionCache`** (`RawDriveImage.cs:100`, `RawPartitionImage.cs:20`) — so no `Files.json`, no
serving decision, and the L4 cache-first short-circuit never applies. The whole-file synthesized
cache folder (keyed on MD5 of first 50 MB decompressed) already exists at the DecompressorSelector
level; it just isn't threaded down to the partitions.

**Lead L6 (affects all 8 drive-image listing tests, warm):** expose the synthesized cache folder
from `DecompressorSelector`, thread it through `CompressedImage` → `RawImage` →
`RawDriveImage`/`RawPartitionImage` so drive-image partitions get real `PartitionCache` instances.
File lists, serving decisions and L4 then apply to all four compressed drive-image formats.
Same umbrella: `RawImage.EnumerateTopLevel` re-scans the partition table via native 7z on every
construction — cacheable in the same folder.

**L6 RESOLVED (2026-07-24): warm 75 s → 21 s (3.6×), Out-Null harness both sides.** Implementation
as designed, plus: `ImageFile` derives an identity folder for bare uncompressed images too (raw
drive/partition tests get the same treatment), `EnumerateTopLevel` caches its scan as
`toplevel.json`, and `GetWholeFileCacheFolder` is memoized — pre-L6 the decision lookup and the
synthesized index cache each hashed 50 MB per open, and each partition hashed another 50 MB
through the decompressor just to locate its decision file in a per-partition synthesized folder
(those folders are now orphans; a cache clear removes them). Validation: miss-path run repopulated
everything (probes + native scans, 222 s), hit-path run listed the identical 829,920 lines with
zero probes/extractor opens; warm trace shows only parked threads, output flushing, and the one
remaining 50 MB whole-image identity hash (~6% incl). Remaining warm floor: Dokan Z: mount wait
(~8 s bucket incl. that hash), 24 MB bzip2-index JSON load (~2 s), 276 MB Files.json STJ parse +
print. Cold pays the same one-off population the partition tests do.

### 6. ListContents.LargeDriveImages.Gz  (cold 4097 s + warm 18 s, private cache)

Clean confirmation that L6 generalizes to gz — no new findings.

**Cold (4097 s; suite 3498 on its best-ever night):** 66.4 min of it is the GzipSeekable index
build (27.6 GB compressed → 2.199 TB uncompressed, 521,229 resume points, 142 fill spans) — the
path exhaustively optimized in the gztool-decommission work (62.7 min measured then; the delta is
the documented environmental swing, daytime vs overnight). Probes + listing + L6 cache population
account for the last ~2 min. Nothing actionable.

**Warm (18 s; suite 60 — 3.3×):** first gz drive-image listing with real partition caches. Faster
than #5's 21 s because the whole-image identity hash decodes 50 MB through gz rather than bzip2,
and the binary `.gzsi` index loads without a JSON parse. Listing content verified byte-identical
to #5's (same underlying image, 829,904 lines, matching MD5 after stripping log lines).

### 7. ListContents.LargeDriveImages.Raw  (cold 48 s + warm 12 s, private cache)

First test through L6's uncompressed-image path (`ImageFile` derives the identity folder from the
raw file itself). Pre-L6 this test had no caching of any kind — cold ≈ warm ≈ 40 s, native 7z
re-scanning the partition table and both filesystems on every open.

**Warm 39.3 → 12 s (3.3×):** toplevel.json + Files.json + serving decisions all hit; no native
opens. Remaining floor is the usual suspects (Dokan mount wait, 276 MB Files.json parse, output).
**Cold 48 s (suite 41.2):** the +7 s is the one-off cache population plus the new 50 MB identity
hash of the raw file (~0.2 s). Listing content verified identical to #5/#6 after stripping the
container-name prefix (raw strips `.img`, compressed strips `.bz2` — cosmetic). No action.

### 8. ListContents.LargeDriveImages.Xz  (cold 258 s + warm 17 s, private cache)

**The biggest L6 win: warm 240 s → 17 s (14×).** Pre-L6 warm equalled cold — with no partition
caches, every open re-listed the NTFS through xz's 32 MiB spans (~16 MiB average decode per
scattered read), the same pathology L4 fixed for partition images. With Files.json cached, warm
never touches the decoder.

**Cold (258 s incl. ~38 s capture overhead; suite 234 s):** multi-block xz serves random access
from its native block index (32,768 blocks, ~20 s to open) — no checkpoint build at all. The bulk
is the one-off native-7z listing through the expensive spans. Content hash identical to #5-#7.
No action.

### 9. ListContents.LargeDriveImages.Zst  (cold 1102 s + warm 41/15 s, private cache)

**Warm: FIXED via L6** — suite 72 s → 15 s once the OS file cache holds the artifacts (native
stdout redirection; same floor as the other formats). The first post-cold warm read 41 s because
the 2.2 TB index build evicts the page cache, and this format has 879 MB of `.zsi` to page back
in (32,768 exact-state points for 2.2 TB; verified the loader skip-parses it lazily — blobs are
seeked past, not read). **Cold (1102 s; suite 930):** 16.2 min ZstdSeekable exact-state index
build + variance. Content hash identical to #5-#8. No zst-specific action.

Parked observation (all drive images): on a warm LISTING the whole-image seekable stream is still
fully constructed (index load + identity hash) even though a cached Files.json means nothing will
ever read from it. Lazy construction would shave a few seconds; only worth revisiting if the
listing floor ever matters more than the Dokan mount wait (~8 s), which is now the biggest chunk.

### 10–14. Partclone + SmallClonezillaPartitions  (batch, 2026-07-24)

All five clean — no new findings, quick paired runs only (no traces; nothing to chase). These
images have real clonezilla cache folders, so the #1–#4 fix set (serving-decision cache, STJ
lists, lazy extractor) was already active: every warm leg sits at 1–3 s, cold ≈ suite within
variance. Cold vs warm listing hashes verified identical per test; all four small-partition
formats list the same 882 lines.

### 15–19. SmallPartitionImages  (batch, 2026-07-24)

L6 at small scale — pre-L6 these partition images had null caches, so every warm open re-paid
the 10 s serving-decision probe plus a native NTFS re-scan. All five warm legs collapsed
(bz2 32→11, gz 16→2, raw 6→1, xz 25→4, zst 8.5→2 s); colds ≈ suite within variance (bz2's 55 s
is its index build). The bz2 warm residual is the 50 MB whole-file identity hash decoding
through sequential bzip2 — same shape as #5, inherent to the identity key. Parity: all five
formats, cold and warm, produce the byte-identical 170-line listing (container prefix stripped).

**ListContents namespace (#1–#19) complete.** Fixes shipped during the sweep: SharedStream length
caching, serving-decision persistence, STJ file lists, lazy extractor (L4), drive-image partition
caches (L6). Next: the Mount namespace (#20+), which exercises the Dokan serving stack the
listings never touch — first candidates for the parked `Utilities.GetMemoryPressure()` polling
observation from #1.

### 20–21. Mount.AsFiles.Ext4  (2026-07-24; harness = suite-faithful mount+poll+MD5 protocol)

**#20 ext4 (raw): clean.** Cold 17 s ≈ suite 15.4; warm 14 → 4 s (L6: tree from cached file list).

**#21 ext4_zst: first real Mount-namespace finding — warm 54.5 → 37 s, improved but not
collapsed.** Warm timeline (redirected diagnostic run): the whole-image serving decision is
"sequential" (the .zst is ~1 MB holding 33 GB of near-zeros), and `CompressedImage` passes a null
uncompressed length, so `SeekableStreamUsingRestarts` discovers the virtual decompressed file's
length by decoding the entire 33 GB — **24 s on every open, cold and warm**. The remaining 11 s
is the eager (D3-mandated) ext4 extractor open, whose backward seeks re-decode through the
restart-based stream. Both expected-file MD5s verified through the mount in all legs.

**Lead L7 (proposed, not implemented): persist the uncompressed length in the whole-file identity
folder.** When `DecompressorSelector` serves a null-length stream, read a cached
`uncompressed_length.txt` and hand it to `SeekableStreamUsingRestarts`; write it the first time
the length is discovered. Kills the 24 s for every "sequential"-decision compressed image
(hits exactly the small-compressed/huge-decompressed case). The 11 s extractor residual would
need the serving-decision heuristic to prefer an index for huge-decompressed images (L8,
riskier — the 10 s probe measures sequential throughput, not random access) — parked unless L7
proves insufficient.

**L7 RESOLVED (2026-07-25): warm ext4_zst mount 37 → 31 s; the length-discovery decode is gone.**
As designed: `SeekableStreamUsingRestarts` gained an `OnLengthDiscovered` callback (fires only on
the read-to-EOF branch); `DecompressorSelector` resolves a cached `uncompressed_length.txt`
(partition cache or identity folder, same placement as the serving decision) before constructing
any restarts stream, and persists on first discovery. All three restarts construction sites now
share one factory. Verified: run 1 wrote 34,359,738,368 (= the 32 GiB image) to the identity
folder; run 2 logged "Using cached uncompressed length" and the whole-image setup gap fell
24 → 11 s. Applies to every sequential-decision compressed image and the no-index fallback path.

**L10 RESOLVED (2026-07-25): warm bare-partclone mount 139 → 110 s; the 41 s layer rebuild is
now 1 s.** Root cause was L6's own defensiveness: `CompressedImage` nulled the identity folder on
the partclone iteration, costing bare-partclone containers their toplevel and partition caches
every open. The partclone-decoded content is a pure function of the compression layer's content,
so keeping that layer's folder is stable and unique — one deleted assignment. Verified: run 2
shows whole-image setup 41 → 1 s (cached toplevel + partition decision + L7 length, all hitting).

**Note on the residual (both #21 and #35): parallel pool opens mildly regress restart-backed
streams** (#21 pool 11→18 s, #35 94→107 s — drifting concurrent scanners occasionally force full
gz restarts), while winning 3× on index-backed streams (bzip2 226→57, xz 280→87). Net across the
suite is strongly positive; the true fix for the restart-backed cases is L8 (prefer an index for
huge-decompressed images regardless of sequential throughput), still parked.

**L8 RESOLVED by removing the probe entirely (2026-07-25).** The 10-second serving-decision
benchmark dated from the gztool/extraction era; with cached in-process indexes for every
mainstream format its only distinctive behavior was choosing "sequential" for
small-compressed/huge-decompressed images — the exact remaining pathology. `GetSeekableStream`
now always serves index-backed; formats without an index (lz4/lzip) fall back to restart-based
seeking exactly as the old "sequential" verdict did, raw FileStreams still serve directly, and
the decision-persistence plumbing (probe, `serving_decision.txt` read/write, the L2 fix it
existed for) is deleted — stale decision files in existing caches are ignored. Verified:
ext4_zst warm mount 31→5 s (one-off 24 s index build), partclone-gz warm 110→91 s (pool scan via
gz-zran; the residual is honest scattered ext4-metadata decode), bzip2 clonezilla mount 85 s ≈
unchanged (index-backed streams unaffected). Cold cost shifts from a 10 s probe to a one-off
cached index build per stream.

### 22. Mount.AsFiles.LargeClonezillaImages.bzip2  (cold 1252 s + warm 267 s, private cache)

Note: the suite's "cold" 8.4 min is index-warm — suite test #1 (ListContents, same image) has
already built the three bzip2 indexes and file lists. The private-cache cold here is a true
from-scratch build (matches #1's index-build time). Honest comparison is warm: 312 → 267 s.

**Warm decomposition (diagnostic run, per-file copy timing):** mount-ready 216 s, then the three
file copies took 0 s, 2 s, 1 s — the Dokan serving stack (CachingStream + bzip2 index reads) is
NOT the problem. 202 of the 216 s is sda2's "Retrieving a list of files" (721,234 files) in
`MountedPartitionImage.GetTree` — with a warm Files.json. That phase is the eager
`DetermineExtractor.FindExtractor` native-7z worker pool construction (`MountedPartitionImage.cs:88`),
which runs BEFORE the cache check (:126): every worker open re-scans NTFS structures through the
bzip2 decoder. L4 fixed exactly this for ListContents but deliberately left the mount flow eager
(D3: never open an archive inside a Dokan callback).

**Lead L9 (to investigate): make mount-time pool population cheap without violating D3.**
Candidates: open ONE worker before mount completes and grow the rest on background threads (not
Dokan callbacks); or serialize the first open so its NTFS-metadata decodes land in CachingStream
before the remaining workers open (turning 12 cold scans into 1 cold + 11 cached). Requires
reading NativeExtractorPool + CODE_REVIEW_PLAN's DokanVFS notes first.

**L9 RESOLVED (2026-07-25): parallel worker opens — warm bzip2 mount 267→84 s (3.2×), xz
309→110 s (2.8×).** Instrumentation overturned the design assumption: the pool already opened
workers sequentially expecting worker 1 to warm the shared CachingStream, but on the 42 GB sda2
the scan cycles the LRU (split ¼-RAM budget), so every later worker re-decoded every range —
measured opens of 61+55+55+54 s (bzip2). Small partitions ([1.4, 0, 0, 0]) masked this. The fix
opens all workers in parallel: CachingStream serves each miss under its cache lock, so concurrent
opens run in natural lockstep — one worker decodes a missed range while the rest block briefly,
then hit it fresh; the pool now costs ~one scan (sda2: 57 s bzip2, 87 s xz). Exceptions unwrap
from AggregateException so callers still see NotAnArchiveException; the D3 invariant (open before
mount completes) is untouched. Remaining warm floor for these mounts is that single scan — a
persistent metadata-region cache could attack it later, noted but not planned.

---

## Golden-reference quartet test (2026-07-28)

New test `Mount/AsFiles/GoldenReferenceQuartet` verifies the PB-DEVOPS1 quartet mounts against
golden MD5 lists produced entirely without clonezilla-util (7-Zip decompress → patched
partclone-for-Windows restore → 7-Zip NTFS extract → md5sum; lists + provenance README live in
`E:\clonezilla-util-test resources\golden reference`, outside git).

**Run 1 (zst, DOP 8, default 4 KB reads, 9 h 56 m): all 558,893 hashable files matched their
independent golden MD5 — zero content errors.** The 342 reported failures were fully accounted
for and are now encoded as test expectations: 321 desktop.ini entries (deliberately filtered out
of the mount by `MountedPartitionImage` so Explorer doesn't hammer it) and 21 colon-named NTFS
alternate-data-stream items (`[SYSTEM]\$Secure:$SDS` etc. plus one DFSR `ContentSet{…}:…` ADS)
that 7-Zip could never extract into the golden lists (':' is illegal in filenames).

**Lead L11 (to investigate): concurrent-read serving corruption under load.** Run 2 (same image,
same exe, but DOP 16 with 1 MB `SequentialScan` FileStreams; 13 h 46 m) produced 32,226 MD5
mismatches + 944 read errors ("A device attached to the system is not functioning") on files that
run 1 hashed cleanly — including two different files returning IDENTICAL wrong content
(cross-file data bleed). The test side cannot corrupt (per-thread FileStreams), so the mount
served wrong bytes under the heavier request pattern. Note the native worker-pool concurrency
ceiling is 4 in both runs — the variable is the Dokan-side pattern (16 pending large reads vs 8
small). Suspects: DokanVFS ReadFile buffer/offset handling for large reads, or the shared
partition-stream path under interleaved large seeks (cf. CODE_REVIEW_PLAN C1). A user doing a
heavy parallel copy (e.g. robocopy /MT) could plausibly hit this. Full failure list preserved at
`%TEMP%\GoldenReferenceQuartet-2022-07-16-22-img_pb-devops1_zst-failures.txt`. The test now pins
DOP 8 + File.OpenRead (the proven-clean pattern).

**Run 3 (2026-07-29): PASSED — 11 h 22 m, zero failures.** Same zst image, reverted read
pattern (DOP 8, `File.OpenRead`), with the desktop.ini/ADS expectations from 3356d4c. All
558,893 hashable files matched their independent golden MD5s, the 321 desktop.ini golden
entries were confirmed absent (the deliberate mount filter), and the extras sweep passed with
colon-named ADS entries skipped. This is the end-to-end proof: clonezilla-util's listing AND
content for the PB-DEVOPS1 zst image are byte-identical to a pipeline that never touches
clonezilla-util code. Runtime (~10-11.5 h per codec) means the quartet belongs in its own
long-running category, not the standard suite; the other three codecs (bzip2/gz/xz) share the
proven-identical compressed payload, so running them adds codec-path coverage rather than
content coverage.

## Why MD5-through-the-mount is slow (profiled 2026-07-29) — Lead L12

Question: run 3 hashed 57.1 GiB in 11 h 22 m (~1.4 MB/s effective at DOP 8), while the
independent pipeline extracted the very same content in under 3 h total (zstd decompress
24.7 MB/s, partclone restore 16 min, 7z extract of all 558k files ≤ 63 min). Where do the
extra ~8.5 hours go?

Method: a scratch console harness reproduced the mount's exact serving stack WITHOUT Dokan
(PartitionContainer.FromPath → SharedStream → NativeExtractorPool) and timed each layer;
client-side benchmarks ran identical workloads against the Dokan mount and against the
extracted tree on native NTFS; dotnet-trace captured 90 s of the mount under golden-style
load (20k files, DOP 8, 4 KB reads); Win32_Process.ReadTransferCount before/after a cold
2,000-file slice measured physical read amplification directly.

### Exonerated (measured, not guessed)

- **Dokan itself**: ~0.2 ms per ReadFile round trip (0.203 ms cold-sequential through the
  mount vs 0.135 ms same pattern in-process), ~2.9 ms per open/close. Across the full run's
  ~14 M reads + 560k opens at DOP 8 that is minutes, not hours.
- **The per-read item reopen** (PooledNativeItemStream opens the 7z item on every Read):
  7 µs per open. Invisible.
- **MD5**: 420 MB/s per core in-memory.
- **Disk**: the mount read the .zst at ~4 MB/s while "busy" — the SSD was idle.
- **The stack when warm**: 4 KB reads through pool.Extract at >1 GB/s; whole-stack
  sequential cold single-stream: 19–29 MB/s; raw partition stream sequential 1 MB reads:
  156 MB/s.

### Convicted: cold-miss economics of the shared decompression stream

The zstd random-access index has resume points every ~64 MB of decompressed output
(ZstdSeekable TargetSpanBytes); CachingStream misses fetch 32 MB-aligned sub-spans
(SeekableZstdStream.GetRecommendation). Serving a cold 4 KB read therefore costs: resume at
the previous point, decode-and-discard up to 32 MB (ZstdFrameHelpers.Skip), then decode the
32 MB sub-span — **measured 292 ms per cold random 4 KB read** (C2). And every miss runs
under CachingStream's single cacheLock, so concurrent misses fully serialize.

The golden workload is the worst case for this design: 8 threads reading 558k files in
golden-list (name) order, 4 KB at a time, over a 139 GB decompressed space with an effective
RAM cache of only ~3.3 GB (¼ RAM ÷ 3 mounted partitions on the 40 GB machine ≈ 104 cache
entries of 32 MB). Files adjacent by name are scattered by cluster, so sub-spans get evicted
and re-decoded over and over.

Key measurements:

| measurement | result |
|---|---|
| 8 files, 4 KB reads, DOP 4, cold, NO Dokan | **1.26 MB/s** (the golden run's rate, reproduced without Dokan) |
| same stack, single stream, cold, sequential 4 KB | 29 MB/s |
| same ranges re-read warm, DOP 8 | 990 MB/s |
| cold random 4 KB at partition level | 292 ms/read |
| cold small file (one scattered read each) | 18–27 files/s (~40–55 ms each) |
| 20k-file golden-order macro, DOP 8: mount vs native NTFS | 939.8 s vs 198.3 s (3.2 vs 15.3 MB/s) |
| physical reads to serve a cold 43 MB slice | 292 MB compressed = **6.7× physical, ~46× decode amplification** |

dotnet-trace under load (1,609 thread-seconds sampled in 90 s wall): 25.7% of thread-time
blocked in Monitor.Enter (cacheLock + Serilog sink lock), 23.1% waiting for one of the 4
native workers (themselves queued behind the lock), 23.8% in the 7z input callback (the
partition stream), but only **~2.6% actually decompressing** — roughly half a core of
productive decode while everything else waits. The pipeline is a queue, not a pipeline.

Secondary finding: the exe logs at MinimumLevel.Debug with a WriteTo.Debug sink, so every
Dokan operation formats a message and calls OutputDebugString under the global sink lock —
**~22% of thread-time under load** (Serilog FilteringSink.Emit 14.9% + DebugProvider.WriteCore
6.7%). Millions of ops pay it.

Why "just copying the files out of the image" is fast: extraction decodes each compressed
byte exactly once, in order, with no per-4 KB locking — the same reason C1 (sequential 1 MB)
runs at 156 MB/s. The golden pipeline's 7z-from-raw-image reads in extent order at SSD speed.

### Cheap wins available (not yet implemented)

1. **Raise the log level / drop the Debug sink for mounts** — recovers most of the ~22%
   logging thread-time and removes a contended global lock from every operation.
2. **Bigger client reads help enormously**: through the mount, 64 KB reads ran 304 MB/s vs
   19 MB/s for 4 KB (warm). The golden test's File.OpenRead uses a 4 KB FileStream buffer;
   a 1 MB buffer at DOP 8 would cut mount-side request count ~256× for large files (note:
   DOP 16 + 1 MB triggered L11 corruption; DOP 8 + large buffer is untested against L11).
3. **Decode outside the lock**: CachingStream serializes the whole miss (seek+decode) under
   cacheLock. Decoding into the entry outside the lock (per-span in-flight registry) would
   let the 4 workers actually run in parallel.
4. **Persist decoded spans to disk** (the whole-file identity cache folder already exists):
   139 GB decoded once at ~156 MB/s is 15 min; the golden run effectively decoded the image
   ~30–45× over.
5. **Per-handle read-ahead** for the overwhelmingly-sequential per-file pattern would turn
   interleaved 4 KB misses back into sequential span decodes.

Harness/bench sources and the 90 s .nettrace live in the session job folder (scratch, not
committed); all numbers above are recorded here so nothing depends on those files surviving.

## Lock-removal work on the serving path (2026-07-29) — acting on L12

Goal: remove/narrow the locks that serialize the mount serving path (L12), so the 4-worker pool
can actually decode in parallel. Landed as independent, individually-verified steps.

**Shipped to master (all verified byte-correct against the golden lists through a live mount):**

- **S0 — logging.** Default `MinimumLevel.Information` + a `--verbose` flag (Program.cs, BaseVerb).
  The exe ran at Debug with a `WriteTo.Debug`/OutputDebugString sink under a global sink lock -
  ~22% of thread-time under load (L12). Console/File sinks already filtered at Information, so
  nothing visible changes.
- **S1 — `IPositionalReader`.** A positional `ReadAt(pos,buf,off,len)` API (libCommon) implemented on
  CachingStream, PartcloneStream, and SeekableZstdStream; each `Stream.Read` becomes a thin wrapper
  that owns the one mutable cursor and delegates to `ReadAt`. Pure refactor, behaviour identical.
- **S2 — lock-free worker feed.** Each 7z worker now reads the partition through its own
  `PositionalCursorStream` (positional `ReadAt`), so the `SharedStream.gate` (held across the whole
  decode - the "illusion of parallelism") and `PartcloneStream.streamLock` are gone from the serving
  path. Throughput is unchanged at this step - the CachingStream lock still serializes every miss.
- **S2b — watchdog use-after-free (a real crash bug this work surfaced).** The slow-read watchdog
  called `IDokanFileInfo.TryResetTimeout` on entries whose ReadFile callback had already returned; a
  DokanFileInfo's native handle is valid only while its callback is on the stack, so a tick racing a
  completing read called `DokanResetTimeout` on a freed handle - a native access violation
  (0xC0000005) the try/catch cannot catch, killing the process. The old SharedStream gate serialized
  the serving path so at most one slow read was ever registered; removing it (S2) made concurrent slow
  reads the norm and the crash reliable under load. Fixed with a per-entry `Completed` flag set (under
  a lock) by the read's Dispose - which runs *inside* ReadFile, while the handle is still valid - and
  checked under the same lock in the timer. This is very likely a contributor to L11's read errors
  ("a device attached to the system is not functioning") and to any heavily-loaded mount's instability.

**NOT shipped — S3 (parallel decode), blocked by L11.** The concurrent single-flight cache (decode a
32 MB span outside the lock; coalesce concurrent readers of one span; copy-under-lock + re-lookup
buffer lifetime so an evicted ArrayPool buffer can't be reused mid-copy) works and gives ~1.5x on the
golden-order macro (612 s vs 939.8 s). But it corrupts ~3% of files in the live mount (cross-file
bleed - two files returning identical wrong content, one file served another's bytes). Preserved on
branch `wip/s3-parallel-decode`.

Extensive isolation proved the corruption is **not** in the new cache/decode code:

| test | result |
|---|---|
| package `ZstdSeekable.Tests.ConcurrentViews` (4 views, MemoryStream) | clean |
| full S3 stack, synthetic 256 MB, DOP 16, tiny budget | clean |
| full S3 stack, **real 19.9 GB stream + real exact-state index**, 42.3 GiB uncompressed, DOP 16 | clean (0/400) |
| maximal collision + sub-span (24 MB < 32 MB) eviction stress, DOP 32, 28,800 reads | clean (0 mismatch) |
| **S2 build (serialized decode)** under the exact stress that bled S3 (400 scattered, DOP 16) | **clean (0/400)** |
| **S3 build in the live mount**, same 400 scattered DOP 16 | **13 mismatches (bleed)** |

So: S2 (decode serialized by the cache lock) is clean; S3 (decode concurrent) corrupts - but only in
the live mount, never in isolation even at real scale and heavier concurrency. The distinguishing
factor is the native 7z worker path above the cache. **Conclusion: concurrent partition *decode*
driven through the native 7z workers triggers a latent race == Lead L11.** The ZstdSeekable decode
path was audited and holds no shared mutable state (per-decode DCtx / window / exact-state / input
buffer / cursor; immutable index; gate-serialized compressed reads), and `Multistream` here wraps a
single 19.9 GB file so it isn't the culprit. The remaining suspect is the native lib7zNative / 7z.dll
behaviour when multiple archive workers decode concurrently - the exclusive per-worker borrow model
*looks* safe by inspection, so this needs a native-level investigation (or a decode serialized behind
the cache, below).

### Recommended next steps

1. **Root-cause L11** (the native 7z concurrent-read race) - it blocks S3 and is a latent
   correctness/stability risk for any heavily-parallel client (e.g. robocopy /MT) even today.
   A cheap first probe: run the S3 branch with `MountWorkerCount = 1` - if clean, it confirms the
   race is between concurrent 7z workers.
2. **S3-safe interim**: keep the single-flight cache but serialize the decode (one at a time behind a
   dedicated decode lock) so *hits and waiters no longer block behind a cold decode*, without ever
   running two decodes at once. Safe (no concurrent decode → no L11 trigger) and captures part of the
   win; needs its own golden re-verification.
3. Larger client read buffers (L12 cheap-win #2) remain available and orthogonal.

### L11 narrowed to the Dokan boundary (2026-07-29)

Follow-up to the S3 blocker above. A Dokan-free harness (`poolverify`) was built to drive the *exact*
mount read path minus Dokan: `PartitionContainer.FromPath` → the same 4-worker `NativeExtractorPool`
fed by `PositionalCursorStream` over the S3 `CachingStream` → `extractor.Extract(path)` →
`PooledNativeItemStream`, reading 600 scattered files at DOP 16 and MD5-checking against golden.

**Result: 0 mismatches.** The full 7z + decode + single-flight-cache stack, at the same concurrency
that corrupts the live mount, is byte-perfect. Combined with the earlier isolation results, this
**exonerates everything below Dokan** and places L11 in the **Dokan layer** — matching its original
suspicion ("DokanVFS ReadFile buffer/offset handling"). It is a pre-existing bug (independent of S3),
which S3 merely exposes by making partition decodes fast and concurrent.

Prime suspects, in order:
1. **The memory-mapped / paging read path** (`DokanVFS.ReadFile` with `info.Context == null` →
   `FileEntry.ReadForMemoryMap`, libDokan/VFS/Files/FileEntry.cs:35). The Windows cache manager issues
   paging reads (no handle context) for read-ahead even under normal `File.OpenRead`, so a mount serves
   a *mix* of per-handle reads and paging reads for the same file, on different streams/locks. This is
   the one shared-state serving path `poolverify` does not exercise.
2. A **DokanNet / Dokan-driver buffer-handling race** under concurrent fast reads — plausible because
   the bug is timing-sensitive (appears only once decode is concurrent/fast, not when serialized).

Next experiment to pin it: on the S3 branch, serialize the paging path against the per-handle path
(one lock per file for both `ReadForMemoryMap` and normal reads) and re-run the mount verify. If clean,
the paging/handle interleaving is the cause; fixing it would also unblock S3 (whose decode/cache is
already proven correct) and remove L11 from today's product.

#### L11 further narrowed: every managed layer exonerated (2026-07-29)

Continued the bisection past the Dokan boundary. Each layer the mount uses that `poolverify` does
not was audited/decompiled:

- **DokanNet 2.3.0.3 buffer pool / adapter** (`DokanOperationsAdapter.ReadFile` → `BufferPool`,
  decompiled): the adapter rents an exact-size `byte[]` from a process-wide `ConcurrentBag` pool,
  passes it to our `ReadFile`, then `CopyTo`s it to the native buffer. `ReturnBuffer` does
  `Array.Clear` before re-pooling, and `RentBuffer` hands out distinct buffers — so no stale data and
  no shared buffer across concurrent reads. **Not the cause.**
- **The path lookup on the paging path** (`RootFolder.GetEntryFromPath` → `Folder.GetChild`): the
  normal read path uses `info.Context` and never looks up by name, but the memory-mapped/paging path
  (`DokanVFS.ReadFile` with `info.Context == null`, which the OS cache manager drives) resolves the
  file by path. `GetChild` is a lock-guarded dictionary lookup, so it returns the correct entry under
  concurrency. **Not the cause.**
- **`FileEntry.ReadForMemoryMap`**: serves the paging path from a per-file reusable stream, correctly
  bound to that file. **Not the cause.**

With the 7z/decode/cache stack, the DokanNet managed layer, and the VFS lookup all proven correct
under concurrency, L11 sits in the **Dokan driver / Windows cache-manager coherency under concurrent
fast reads** — below our managed code. S3 exposes it by making decodes fast and parallel (so the cache
manager drives many concurrent paging reads); the serialized-decode S2 build spaces reads out enough
to (nearly) never hit it. This is consistent with L11 being pre-existing and with the golden test
avoiding it via DOP 8 + `File.OpenRead`.

**Options for the user to weigh (each a trade-off, none free):**
- Disable OS caching for the mount (serve every read directly, no cache-manager paging). Would likely
  sidestep the driver race, but removes OS caching of hot pages (e.g. the `$MFT`) - a real throughput
  cost that must be measured.
- Implement `IDokanOperationsUnsafe` (read straight into the native buffer, bypassing the managed
  adapter). Clean and slightly faster, but the adapter was shown correct, so this alone is unlikely to
  fix the driver-level race.
- Investigate/curate the Dokan driver version (the race is in unmanaged Dokan/cache-manager code).

Net: S3's decode/cache is correct and ready; shipping it needs the Dokan-layer race resolved first.

### L11 ROOT CAUSE FOUND: S3 single-flight starved the worker pool (2026-07-29)

The earlier "S3 corrupts the mount" was chased to ground. It is **not** the Dokan driver and
**not** the decode/cache correctness (every isolation test is byte-perfect). It is a flaw in the
S3 single-flight design, triggered by a client dying mid-read.

**How it was found.** A Dokan-free harness driving the exact 7z + decode + cache path at DOP 16
was clean; so was one exercising `FileEntry.ReadForMemoryMap`; the DokanNet buffer pool (decompiled)
clears buffers and hands out distinct ones. A fresh S3 mount was clean, and macro-load + verify was
clean. The one run that ever bled (verify4, 13/400) had followed a **force-killed** client. Replaying
that exactly reproduced it: **S3 + kill = 20 mismatches + 22 "Insufficient system resources" errors**;
**S2 + kill = clean**; **S3 without a kill = clean**. Reverse-mapping the wrong hashes showed genuine
cross-file bleed (files served other files' content, in chains).

**Mechanism.** S3's single-flight coalescing made a reader that found a span already being decoded
block in `slot.Ready.Wait()` **while holding its native 7z worker** (the wait sits deep inside the 7z
read, under the pool borrow). Under same-span contention - and especially the burst when a client is
killed mid-read - waiters pin all 4 workers, so other reads can't get one and hit the Dokan timeout
(0x800705AA). A timed-out `ReadFile` then completes and writes its bytes **late**, into a buffer Dokan
had already reclaimed and reissued for a different file - cross-file bleed. S2 has no
waiters-holding-workers, so no starvation, no timeouts, no late write. That the resource errors and
the bleed appear together (and only in S3+kill) is the signature.

**Why this invalidated the earlier attribution.** The original "S2 clean vs S3 corrupt" comparison was
not apples-to-apples: the S3 test happened to run right after a client kill, the S2 test did not. The
differentiator was the kill interacting with S3's worker-holding wait, not S3's decode.

**Fix** (branch `wip/s3-parallel-decode`, commit after this note): remove the in-flight registry and
the blocking wait; each miss decodes its span independently (a worker is held only during productive
decode, never across a block) and publishes unless another reader cached it first. Bounded redundant
decode instead of a blocking coalesce. Buffer lifetime unchanged (still copy-under-lock, return only
under mapLock after map removal).

**Validation still owed** (this session's machine degraded to ~15 min sda2 mount times, so it must run
fresh): S3-fixed + kill must go clean, then a full golden-quartet zst run. Also worth noting
independently: `ReleaseContext` disposes a per-handle stream without the read lock - a disposal-vs-read
race that is benign today but should be tightened.

### S3 fix validated (2026-07-30, VMs stopped to free the host)

The fix landed in two steps on `wip/s3-parallel-decode`:
- dd4283e (naive): removed coalescing entirely. Stopped the bleed but **regressed mount time
  catastrophically** - the parallel worker-open (L9) needs 4 workers to coalesce on the shared ~1 GB
  $MFT decode; without it each re-decoded the whole $MFT, pushing sda2 mount from ~50 s to 15+ min.
- 1ece201 (fix): gate the coalesce-wait on `CachingStream.SuppressCoalesceWait`, a thread-static flag
  `PooledNativeItemStream` sets for its worker borrow. A coalescing wait only starves the pool when the
  waiter holds a scarce worker (the serving path) - so the serving path decodes its own copy and drops
  it instead of waiting, while the mount-time open (holds no pool worker) still waits and coalesces.

**Validated (fresh-ish host, 24 GB free):**
- Mount ~51 s (sda2 workers 32.7 s = one coalesced scan) - regression gone.
- The kill sequence that reliably corrupted the old S3 (20 mismatches + 22 "Insufficient system
  resources" errors, 1257 s) now runs **clean, twice**: 798/798 and 600/600, 0 mismatches, 0 errors,
  normal speed (~350 s).
- Macro throughput 890 s vs the 940 s S0 baseline - no regression (serving-side independent decode
  gives back some of the unsafe-coalescing 612 s, so the net win over baseline is small on this
  localized workload; scattered access should benefit more; clean numbers need a non-degraded host).

**Still owed before merge to master:** a full golden-quartet zst run (~11 h) as the definitive
correctness gate. Also worth tightening independently: `ReleaseContext` disposes a per-handle stream
without the read lock.

### Full quartet golden validation GREEN — branch merged (2026-08-01)

Both debts above are paid: the `ReleaseContext` tidy-up landed (0135637, dispose under the
per-handle read lock), and the definitive gate ran — **all four codecs, full golden MD5 sweep,
zero failures**. `wip/s3-parallel-decode` merged to master 2026-08-01.

| Codec | Serving path exercised | Wall time | Verified (sda1/sda2/sdb1) | Failures |
|---|---|---|---|---|
| zst (2026-07-30) | **S3 concurrent single-flight** (`IPositionalReader`) | **1 h 54 m** | 559,214 total | 0 |
| bzip2 | legacy serialized (`SharedStream` + wide cacheLock) | 17 h 14 m | 112 / 558,554 / 548 | 0 |
| gz | legacy serialized | 2 h 7 m | 112 / 558,554 / 548 | 0 |
| xz | legacy serialized | **21 h 24 m** | 112 / 558,554 / 548 | 0 |

1,677,642 golden comparisons in the bzip2+gz+xz run alone (plus zst's earlier pass) —
byte-identical throughout, `EXIT=0`, clean teardown. All runs fully cold (fresh exe per codec,
separate source image + cache folder per codec, ~139 GB decompressed content vs a few-GB RAM
budget ⇒ constant eviction — i.e. exactly the cold-miss + eviction regime L11 lived in).

**Finding — xz is the slowest codec through the mount, worse than bzip2** (21.4 h vs 17.2 h),
despite LZMA2 decoding ~5-10× faster than BWT per byte. Per-byte decode speed doesn't explain it;
the plausible driver is resume overhead × amplification: each cold span reinstates a 4 MiB dict
window + probability model from the `.xzi` snapshot index before decoding forward
(`XzIndexedStream`), and the L12 re-decode amplification multiplies that per-resume cost, on a
machine whose asymmetric RAM config already penalizes LZMA2 (~1.16×, measured 2026-07-09). Not
profiled to a split yet — worth a measurement pass when Batch 10 reaches xz. Consequence
recorded in PERFORMANCE_PLAN Batch 10: xz's parallel-decode payoff is larger than the naive
"bzip2 > xz > gz" decode-rate ordering suggested (bzip2 still first — it also gains, and its
bridge unblocks the same machinery).

**Net position:** the serving stack is validated end-to-end on all four codecs; zst additionally
validates the concurrent path under the harshest regime. Follow-on queued as
**PERFORMANCE_PLAN Batch 10** (extend `IPositionalReader` to bzip2/xz/gz bridges).

## L11 ROOT CAUSE FOUND AND FIXED (2026-08-06) — Dokan returns a foreign file's context

**The bug.** Under a client-kill burst, the mount served **the right bytes for the wrong file**.
`DokanVFS.ReadFile` trusts `info.Context` (a `FileEntryStream`) to identify the file being read.
The driver hands back a context belonging to a **different** file, so a read for X is satisfied
from Y's stream. Proven directly, not inferred — a diagnostic logged matched pairs:

```
requested '\sda2\...\8c5255eb....cat'  but this handle's FileEntry is 'PolicyConfigSource.js'
same FileEntryStream instance first served '...\PolicyConfigSource.js',
                                  now asked for '...\8c5255eb....cat'
```

**The fix** (`libDokan/VFS/DokanVFS.cs`, 39 lines):
1. `CreateFile` drops any inherited `info.Context` on entry. It is assigned on exactly one path
   (`fileSystemEntry is FileEntry`) but several paths return before it, and a killed client never
   gets `Cleanup`/`CloseFile`, so a stale `FileEntryStream` could survive into a reused slot. Left
   null, `ReadFile`'s null-context path resolves the entry **by name** — correct by construction.
   Dropped, not disposed: these per-handle streams hold no scarce resource, and disposing one that
   another thread may still be reading would trade corruption for a crash.
2. `ReadFile` verifies the context's `FileEntry.Name` matches the requested `fileName`, and serves
   **by name** when it does not — turning silent cross-file corruption into a correct read.

**Evidence.** A rate-measuring harness (`run-repro-l11.sh`: fresh mount, 6 kill bursts of 3×DOP-16
scattered readers = 48 concurrent vs 4 native workers, then dense + scattered detection passes):

| Build | Trials | Reproduced | Notes |
|---|---|---|---|
| unfixed | ~50% baseline | 3/6, 4/6, 1/2, 5/5 … | 133–323 mismatches per run |
| **fixed** | **28** | **1** | that one was a *different* signature (below) |

Speed-matched pair, which controls for the machine-drift confound that repeatedly misled this
investigation: unfixed **5/5** at 21.1–25.3 MB/s vs fixed **0/12** at 22.5–26.9 MB/s. Across the
final clean-build run the guard corrected **846** reads while reproducing **0/8** — the race fires
constantly and is neutralised, rather than being absent.

### ⚠ This is partly a WORKAROUND, not a cure — revisit

Part 2 detects and corrects; it does not stop the driver handing us a foreign context. Part 1
removes *our* ability to leak a stale context, yet **846 wrong contexts still arrived**, which means
they are not coming from our `CreateFile` at all: the driver (or DokanNet's context marshalling)
associates a context with a file object across different files without a fresh `CreateFile`.

Open questions for a later pass:
- **Why** does `info.Context` come back foreign? Prime suspect is DokanNet's context marshalling
  (the native side stores a handle/index that can be stale or recycled) rather than the kernel
  driver. Worth instrumenting DokanNet's context table, and reporting upstream if confirmed.
- Can we stop *depending* on `info.Context` for identity at all — e.g. key the handle table
  ourselves and treat the driver's context as a hint? That would make the guard unnecessary.
- **Residual, separate bug:** 1 of 28 fixed trials still produced a single wrong file, but with a
  **garbage** signature (`[got matches no golden file]`) rather than a clean cross-file copy.
  Different, much rarer mechanism. Leading candidate is the hypothesis formed first and discarded:
  a timed-out `ReadFile` writing **late** into a buffer the driver already reclaimed — which fits
  rare *partial* corruption, though it never fitted the cross-file pattern. Tracked, not a blocker.

### Method note (worth keeping)

Five wrong theories died here — late-write, persistent cache poison, byte-buffer pool, worker-pool
bookkeeping, dirty-worker recycling — and every one was killed by instrumentation, never by reading
code. Two recurring traps: (1) **cross-time A/B is invalid** — reproduction drifted from 100% to 0%
over a day, so arms must be paired/interleaved or speed-matched; (2) **check a probe's statistical
power before trusting its silence** — a 1-in-64 sample against a <1% event had an expected yield of
0.8 hits, so its zero carried no information.

## L11 CAUSE FOUND (2026-08-08) — we free a GCHandle the driver is still quoting

The 2026-08-06 section above asked *why* Dokan hands back a foreign context. Answered, at three
levels: the library source names the mechanism, the native source names the race window, and a
raw-value tracer caught the whole chain live.

**What `info.Context` actually is** (DokanNet 2.3.0.3, `DokanFileInfo.cs`): a raw `GCHandle`
number stored in the per-event native `DOKAN_FILE_INFO` struct. The setter `GCHandle.Free()`s any
current value and `GCHandle.Alloc()`s the new one; the getter does
`((GCHandle)(nint)context).Target` with **no validation of any kind**. Our callbacks touch that
native struct directly (`DokanFileInfoAdapter` wraps a `DokanFileInfo*` via `Unsafe.AsPointer`).

**The race window** (dokany 2.3.1, the installed native library):
- `SetupIOEventForProcessing` (dokan.c) copies the per-open `UserContext` into **each event's own
  struct** at dispatch — so every in-flight operation carries an independent copy of the number.
- `ReleaseDokanOpenInfo` (dokan.c) writes each completing event's copy **back** to the shared
  `UserContext`, unconditionally, last-writer-wins.
- The user `CloseFile` callback is **deferred until `OpenCount == 0`** — dokany guarantees the open
  is quiescent at CloseFile, and only there. Nothing protects `Cleanup`.

**The bug was ours**: `DokanVFS.Cleanup → ReleaseContext → info.Context = null` **freed the
GCHandle at Cleanup**, exactly when a killed client's slow reads are still in flight carrying
copies of the number. The CLR recycles a freed handle slot on the next `GCHandle.Alloc` — measured:
9,914 allocs reused just **108 distinct raw values**, so recycling is near-immediate under churn. A
concurrent `CreateFile` for another file lands on the freed slot, and the stale read's next
`info.Context` dereference resolves to **that other file's live stream**. That is the entire
cross-file bleed. It compounds: stale events completing later write the dangling number back into
`UserContext`, and `CloseFileProxy`'s `finally { Context = null }` then **frees it a second time**
— detonating whichever live handle owns the slot by then, poisoning further opens. Hence
self-worsening, restart-clears, kill-storm-triggered, and hundreds of guard hits per run.

**Caught live** (`ContextForensics`, `CLONEZILLA_CTX_FORENSICS=1`, raw values read through the
native struct without dereferencing; run-repro-l11 trial, first minute): value `0x8000982228`
logged `alloc ftpsvc.mfl → free (Cleanup) → alloc …ndiswan….manifest → alloc connect-local.jpg`
— the same number allocated **twice with no intervening free** (a stale op double-freed the
manifest's live handle) — then `foreign-read: asked …manifest, got connect-local.jpg`, then the
manifest's release **cross-freeing connect-local.jpg's stream**. A later kill of 25 readers + the
mount produced ~386 foreign reads in under a minute — the storm on demand.

**Two 2026-08-06 assumptions corrected by the data:**
- `create-inherited = 0` across every run: contexts **never arrive at CreateFile** (dokany zeroes
  both its pooled `DOKAN_IO_EVENT` and `DOKAN_OPEN_INFO` structs on reuse — dokan_pool.c). Part 1
  of the 08-06 fix was inert insurance; "the driver reuses the slot" was the wrong mental model.
- "Prime suspect is DokanNet's context marshalling" was half right: the marshalling design is the
  *hazard* (unvalidated GCHandle round-trip), but the *trigger* was our own free-at-Cleanup.

**The cure** (replaces the workaround's role; the guards stay as defense-in-depth):
1. **Free at CloseFile only.** `Cleanup` no longer touches the context; dokany's own
   `OpenCount == 0` deferral makes CloseFile the one point where no in-flight event can hold a
   copy. This removes the *cause*.
2. **Never `Free()` a distrusted number.** New `DokanRawContext.TryZero` clears the native field
   *without* `GCHandle.Free` (deliberately leaking one small object beats freeing another open's
   live handle). Used by: CreateFile's opening scrub, ReadFile's foreign-context guard (which now
   also self-heals the open — subsequent ops take the by-name path and CloseFileProxy has nothing
   left to double-free), and a new CloseFile name-guard.

**Upstream**: worth reporting to dokan-dev — any DokanNet filesystem that assigns `info.Context`
in `Cleanup` (or frees it there) is exposed; the unvalidated `GCHandle` round-trip turns a
lifecycle mistake into silent cross-file data corruption. dokany's unconditional `UserContext`
write-back is what lets a stale copy resurrect after being cleared.

**Validation (2026-08-08)** — three independent layers, all clean:
1. *Repro harness* (16:45–17:13): cure build, 4 trials — **0 mismatches, 0 read errors, and 0
   foreign context events of any kind** across ~92,000 alloc/free cycles and 24 kill-bursts
   (per-trial forensics: ~22–24k allocs, foreign-reads=0, foreign-releases=0, non-stream=0,
   create-inherited=0; both guards silent). The baseline build the night before showed 5 foreign
   reads pre-storm and ~386 in a single kill-storm on the same recipe.
2. *Gate 1, DOP-24 bleed stress* (17:13–19:20): gz + zst + bzip2, 4 phases each incl. the
   post-kill dense passes that were the original reproducers — **12/12 phases, 0 mismatches,
   0 errors**, forensics zero-foreign on every mount (bzip2 alone: 25,229 allocs, 0 foreign).
3. *Gate 2, chunked test suite* (19:27–20:24): **71/71 passed, 0 failed, 0 aborts** against the
   cure working tree (GoldenReferenceQuartet excluded as its own ~40h gate, unchanged).
The race is removed at the source, not corrected after the fact.
