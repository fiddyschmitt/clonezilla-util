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
