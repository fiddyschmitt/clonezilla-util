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
| 3 | ListContents.LargeClonezillaPartitions.Xz | 27 min | 3.1 min | | |
| 4 | ListContents.LargeClonezillaPartitions.Zst | 5.2 min | 1.3 min | | |
| 5 | ListContents.LargeDriveImages.Bzip2 | 14.5 min | 2.2 min | | |
| 6 | ListContents.LargeDriveImages.Gz | 58.3 min | 1 min | | |
| 7 | ListContents.LargeDriveImages.Raw | 41.2 sec | 39.3 sec | | |
| 8 | ListContents.LargeDriveImages.Xz | 3.9 min | 4 min | | |
| 9 | ListContents.LargeDriveImages.Zst | 15.5 min | 1.2 min | | |
| 10 | ListContents.Partclone.MixedPartcloneFormats | 4.5 sec | 3.1 sec | | |
| 11 | ListContents.SmallClonezillaPartitions.Bzip2 | 34.2 sec | 33 sec | | |
| 12 | ListContents.SmallClonezillaPartitions.gz | 10.5 sec | 5.8 sec | | |
| 13 | ListContents.SmallClonezillaPartitions.xz | 27.9 sec | 25.9 sec | | |
| 14 | ListContents.SmallClonezillaPartitions.zst | 10.4 sec | 7.5 sec | | |
| 15 | ListContents.SmallPartitionImages.Bzip2 | 43.2 sec | 32.3 sec | | |
| 16 | ListContents.SmallPartitionImages.gz | 15.9 sec | 15.8 sec | | |
| 17 | ListContents.SmallPartitionImages.raw | 4.5 sec | 6.2 sec | | |
| 18 | ListContents.SmallPartitionImages.xz | 48.3 sec | 24.7 sec | | |
| 19 | ListContents.SmallPartitionImages.zst | 8.1 sec | 8.5 sec | | |
| 20 | Mount.AsFiles.Ext4.ext4 | 15.4 sec | 14.1 sec | | |
| 21 | Mount.AsFiles.Ext4.ext4_zst | 54.3 sec | 54.5 sec | | |
| 22 | Mount.AsFiles.LargeClonezillaImages.bzip2 | 8.4 min | 5.2 min | | |
| 23 | Mount.AsFiles.LargeClonezillaImages.gz | 1.1 min | 1.1 min | | |
| 24 | Mount.AsFiles.LargeClonezillaImages.xz | 6.9 min | 7.1 min | | |
| 25 | Mount.AsFiles.LargeClonezillaImages.zst | 1.6 min | 1.7 min | | |
| 26 | Mount.AsFiles.LargeDriveImages.bzip2 | 2.3 min | 2.4 min | | |
| 27 | Mount.AsFiles.LargeDriveImages.gz | 1 min | 1.1 min | | |
| 28 | Mount.AsFiles.LargeDriveImages.Raw | 39.4 sec | 37.9 sec | | |
| 29 | Mount.AsFiles.LargeDriveImages.xz | 4 min | 4.5 min | | |
| 30 | Mount.AsFiles.LargeDriveImages.zst | 1.3 min | 1.3 min | | |
| 31 | Mount.AsFiles.LuksClonezillaImages.luks_ext4_500GB_gz | 34 sec | 29.9 sec | | |
| 32 | Mount.AsFiles.LuksClonezillaImages.luks_ext4_500GB_zst | 21 sec | 21 sec | | |
| 33 | Mount.AsFiles.LuksClonezillaImages.luks_ntfs_20GB | 3.1 sec | 2.2 sec | | |
| 34 | Mount.AsFiles.LuksClonezillaImages.luks_ntfs_6GB | 2.2 sec | 3.2 sec | | |
| 35 | Mount.AsFiles.LuksParcloneImages.luks_ext4_500GB_gz | 3 min | 2.6 min | | |
| 36 | Mount.AsFiles.LuksParcloneImages.luks_ext4_500GB_zst | 3.1 min | 2.8 min | | |
| 37 | Mount.AsFiles.LuksParcloneImages.luks_ntfs_20GB | 12.2 sec | 12.2 sec | | |
| 38 | Mount.AsFiles.LuksParcloneImages.luks_ntfs_6GB | 10.2 sec | 35.9 sec | | |
| 39 | Mount.AsFiles.Misc.LastestClonezilla_2022_06_29 | 3.1 sec | 3.2 sec | | |
| 40 | Mount.AsFiles.Misc.MultipleContainers_MultiplePartitions | 40 sec | 15.4 sec | | |
| 41 | Mount.AsFiles.Partclone.dd | 112.4 min | 1.7 min | | |
| 42 | Mount.AsFiles.Partclone.gz | 1.4 min | 18.7 sec | | |
| 43 | Mount.AsFiles.Partclone.MixedPartcloneFormats | 7 sec | 2.2 sec | | |
| 44 | Mount.AsFiles.Partclone.PartcloneImage | 6.2 sec | 4.2 sec | | |
| 45 | Mount.AsFiles.SmallClonezillaPartitions.Bzip2 | 37.2 sec | 35.2 sec | | |
| 46 | Mount.AsFiles.SmallClonezillaPartitions.gz | 10 sec | 3.2 sec | | |
| 47 | Mount.AsFiles.SmallClonezillaPartitions.LZ4 | 4.2 sec | 3.1 sec | | |
| 48 | Mount.AsFiles.SmallClonezillaPartitions.LZIP | 5.2 sec | 3.2 sec | | |
| 49 | Mount.AsFiles.SmallClonezillaPartitions.Uncompressed | 2.1 sec | 2.1 sec | | |
| 50 | Mount.AsFiles.SmallClonezillaPartitions.xz | 29 sec | 24 sec | | |
| 51 | Mount.AsFiles.SmallClonezillaPartitions.zst | 8.6 sec | 8.3 sec | | |
| 52 | Mount.AsFiles.SmallPartitionImages.Bzip2 | 32.6 sec | 33.6 sec | | |
| 53 | Mount.AsFiles.SmallPartitionImages.gz | 37.4 sec | 38.3 sec | | |
| 54 | Mount.AsFiles.SmallPartitionImages.Raw | 6.2 sec | 8.2 sec | | |
| 55 | Mount.AsFiles.SmallPartitionImages.xz | 51.2 sec | 26.4 sec | | |
| 56 | Mount.AsFiles.SmallPartitionImages.zst | 31.1 sec | 4.2 sec | | |
| 57 | Mount.AsFiles.UbuntuFileSystems.ext4 | 4.7 min | 2 min | | |
| 58 | Mount.AsFiles.UbuntuFileSystems.ext4_lvm | 5.2 min | 2.3 min | | |
| 59 | Mount.AsImageFiles.ImageFileTests.Gz | 19.2 sec | 14.5 sec | | |
| 60 | Mount.AsImageFiles.ImageFileTests.LuksNtfs6GB | 51 sec | 51.1 sec | | |
| 61 | Mount.AsImageFiles.ImageFileTests.Partclone | 9.9 sec | 10.8 sec | | |
| 62 | Mount.AsImageFiles.ImageFileTests.UncompressedPartitionImage_and_gzClonezillaImage | 16.9 sec | 17.3 sec | | |
| 63 | Mount.AsImageFiles.ImageFileTests.Zst | 11.9 sec | 6.1 sec | | |
| 64 | Partclone.PartcloneContentMapTests.EdgePatterns | 813 ms | 856 ms | | |
| 65 | Partclone.PartcloneContentMapTests.V1_Typical | 78 ms | 35 ms | | |
| 66 | Partclone.PartcloneContentMapTests.V2_DeviceLargerThanBitmap | 1.2 sec | 940 ms | | |
| 67 | Partclone.PartcloneContentMapTests.V2_LargeBlocksPerChecksum_RunSpansManyStrips | 54 ms | 38 ms | | |
| 68 | Partclone.PartcloneContentMapTests.V2_NoChecksum_Typical | 38 ms | 30 ms | | |
| 69 | Partclone.PartcloneContentMapTests.V2_PartialLastBlock | 32 ms | 28 ms | | |
| 70 | Partclone.PartcloneContentMapTests.V2_WithChecksum_Typical | 39 ms | 39 ms | | |
| 71 | Sparse.SparseTests.ExtractAndSparsifyFile | 13.6 min | 23.1 min | | |

## Findings

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
