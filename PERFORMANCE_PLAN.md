# Performance Improvement Plan (2026-06-14)

Performance-focused review of clonezilla-util. Companion to `CODE_REVIEW_PLAN.md` (correctness).
Findings are grouped into **independent batches**; the full unit-test suite (~10h) is run after each
batch to confirm no regression. Batches do **not** depend on each other, so they can be done in any order.

Legend: `[ ]` pending, `[x]` done, `[skip]` deliberately not done.
Risk: **none** (byte-identical output) / **low** / **medium** (touches data-path or concurrency).

Hot paths that matter:
- **Mount / tree-build:** millions of `ArchiveEntry` → `Folder`/`FileEntry` nodes (one-time, but O(n²) traps hurt).
- **Dokan callbacks:** every `ReadFile`/`FindFiles`/`GetFileInformation` resolves a path + reads the stream stack.
- **Index build:** bzip2 block finding + `LazyList` force-eval over a whole compressed image.

Measurement: the existing `unit test stats` timing logs already capture mount/list duration — record the
headline timings before/after each batch there so the win (or regression) is visible.

---

## Batch 1 — Folder lookup overhaul  (highest value, isolated because highest risk)

Turns mount-time **O(N²)→O(N)** and per-call path resolution **O(depth·width)→O(depth)**. Isolated in its
own batch so a failed 10h run is unambiguously attributable.

- [x] **P1. Dictionary-back `Folder`** — `libDokan/VFS/Folders/Folder.cs`, consumed by
  `RootFolder.GetEntryFromPath`. **(done 2026-06-14, builds clean — awaiting 10h test run)**
  - Today: `AddChild` does `children.Contains(entry)` = **O(n)** → building an N-child folder is **O(N²)**
    (WinSxS = 100k+ entries). `GetEntryFromPath` linear-scans `.OfType<Folder>().FirstOrDefault(name)` then
    `.OfType<FileEntry>()` per path component (+ closure/iterator alloc), on essentially every Dokan call.
    `Children` getter copies the whole list on every access (the thread-safety snapshot from CODE_REVIEW C5).
  - Done: kept the ordered `List` as source of truth for enumeration; added a reference-identity
    `HashSet<FileSystemEntry>` (O(1) de-dup + recursion guard) and a
    `Dictionary<string, FileSystemEntry>(StringComparer.OrdinalIgnoreCase)` name→first-inserted index; added
    `FileSystemEntry? GetChild(string)`; `GetEntryFromPath` now uses it (O(1), no alloc, no scan).
  - **Implementation note:** the original design note (below) used only list+dict, but a name-keyed guard would
    *infinite-loop* when two distinct same-name siblings exist (the `Parent` setter re-enters `AddChild`).
    The reference-identity `HashSet` is what de-dups and terminates that recursion — see updated design note.
  - **Behaviour:** identical for all real inputs. The one theoretical divergence is a *folder and a file with
    the same name in the same directory* (impossible on real ext4/ntfs/xfs sources): the old code preferred the
    folder; the new code returns whichever was inserted first. Not reachable from real partition images.
  - Cost: ~2 extra references per node (HashSet + Dict entries). Negligible vs the FileSystemEntry nodes
    themselves; the HashSet is only needed during build but is kept for the node's lifetime (no "build done" signal).
  - **Risk: medium.**

**Verification:** full suite. Pay attention to any test image with case-distinct siblings in one folder
(e.g. `README`/`readme`) and to file-count assertions after mount. **If no test image has such a sibling pair,
that case is unverified — worth adding one.**

---

## Batch 2 — Hot-path logging & cheap allocations  (broad, lowest risk)  **(done 2026-06-15, builds clean — awaiting 10h test run)**

Only touches log output and per-call allocations; no data-path change.

- [x] **P2. Stop eager `Log.Debug($"…")` rendering on hot paths.**
  - **Key fact:** the production config is `MinimumLevel.Debug()`, so `Log.IsEnabled(Debug)` is **true** — a
    runtime gate would be a no-op today. So the per-read/per-item logs were **deleted** (the plan's endorsed
    alternative), which is a guaranteed win regardless of config. Each deleted `Log.Debug` removes, per hot
    iteration: eager `$"…"` interpolation + `BytesToString` + `LogEvent` alloc + the global
    `SuppressConsecutiveDuplicateFilter.RenderMessage()` (it re-renders every event to dedupe) + Debug-sink dispatch.
  - Deleted: `CachingStream` "Cache miss"/"Cache hit"/"Want to read…" (per read); `SeekableDecompressingStream.Read`
    "Attempting to read"/"Finished reading" **and the two `DateTime.Now`** (timing removed entirely — no Stopwatch
    needed); `LazyList.EnsureExists`/`GetEnumerator` per-item logs.
  - `DokanVFS.Trace` (both overloads): added `if (!Log.IsEnabled(LogEventLevel.Debug)) return result;`.
    **This is future-proofing only — no win while the global level is Debug.** The real lever for the per-op
    Trace cost is raising `MinimumLevel` to `Information` in `Program.cs` (your file — not touched here);
    do that and the guard starts paying off (and the `"out "+bytesRead` arg building at call sites would then
    be the only remaining per-op cost, gateable later if it matters).
  - **Risk: low** (logging only; behaviour-neutral).
- [x] **P3. `BytesToString` allocated its units array every call** — `libCommon/Extensions.cs`.
  Hoisted to `static readonly string[] ByteUnits`. **Risk: none.**
- [x] **P12. `Buffers.ARBITRARY_LARGE/HUGE_SIZE_BUFFER` were branching properties** — `libCommon/Buffers.cs`.
  Now `static readonly` fields resolved once at static init (declaration order LARGE→HUGE→BufferPool preserved,
  so no forward-reference-to-default trap). **Risk: none.**
- [x] **P13. Dead code:** removed unused `distanceFromCurrentPosition` in `DokanVFS.ReadFile`. **Risk: none.**

**Verification:** full suite (mostly to confirm the deletions didn't alter control flow). No test asserts on
Debug log content, so this should be a clean pass; the win is reduced CPU/alloc on read/index hot loops, which
may or may not surface above the suite's decompression-dominated noise.

---

## Batch 3 — Archive listing & index-build path  **(done 2026-06-16, builds clean — awaiting 10h test run)**

All affect the `list` / tree-build / bzip2-index path, so one 10h run exercises them together.

- [x] **P6. Culture-sensitive `StartsWith` in 7z parsing** — `lib7Zip/SevenZipUtility.cs`.
  `line.StartsWith("Path =")` etc. defaulted to `CurrentCulture` (much slower than ordinal) and run over
  **every** output line for archives with up to millions of entries. Added `StringComparison.Ordinal` to all
  eight `StartsWith` calls in the file (`GetArchiveEntries` Path/Size/Offset/Modified/Created/Accessed, plus the
  `Path =` checks in `GetArchivesInFolder` and `IsArchive`). The `Folder = +` / archive-name `Equals` checks were
  left untouched — `string.Equals(string)` is **already ordinal** by default (only `StartsWith(string)` is
  culture-sensitive). **Bonus (`line.Replace(...)` → slice) deliberately skipped:** `Replace` strips all
  occurrences whereas a prefix-slice strips only the prefix — equivalent for real 7z output but a behavioural
  divergence on a pathological value containing the token, and slicing a token-only line with no trailing value
  would throw. Not worth the risk for a one-time listing path; the culture→ordinal change is the real win.
  **Risk: none** (ordinal vs culture is identical for these ASCII literals).
- [x] **P5. Boyer-Moore rebuilt tables every call** — `libDecompression/Utilities/BoyerMoore.cs`,
  `libBzip2/BZip2BlockFinder.cs`. `MakeCharTable`(256) + `MakeOffsetTable` were rebuilt on every `IndexOf`, and
  `BZip2BlockFinder.FindInstances` calls `IndexOf` once per buffer-subsection over the whole compressed stream
  with a **constant** needle (`StartOfBlockMagic`). Added a `BoyerMooreSearcher` class that precomputes both
  tables in its constructor and exposes `IndexOf(Span<byte>)`; `FindInstances` now builds one searcher per call
  and reuses it for every subsection. Kept the static `BoyerMoore.IndexOf(Span<byte>, byte[])` as a convenience
  wrapper (builds a searcher then searches) so the public API is unchanged. The core search loop and table
  construction are byte-for-byte the same code, just hoisted — generated `*.bzip2_index.json` must be identical.
  **Risk: low/medium** (index-path refactor — verify generated bzip2 indexes are identical to before).
- [x] **P11. `RegexOptions.Compiled` on single-use patterns** — `libDokan/FindFilesPatternToRegex.cs`
  (`Convert`). Dropped `Compiled` on the per-call dynamic pattern (kept `IgnoreCase`); the static reused regexes
  (`HasQuestionMarkRegEx`/`IllegalCharactersRegex`/`CatchExtensionRegex`) keep `Compiled`. `Compiled` only pays
  off over many matches; for use-once enumeration patterns the heavy IL emit is a net pessimization.
  **Risk: none** (identical match results).

**Verification:** full suite. Confirm `list` output and bzip2 `*.bzip2_index.json` contents are unchanged.

---

## Batch 4 — Stream-stack & traversal micro-opts  **(done 2026-06-16, builds clean — awaiting 10h test run)**

- [x] **P4. `CachingStream` lookup + RAM accounting** — `libCommon/Streams/CachingStream.cs`.
  Replaced the per-read `cache.FirstOrDefault(entry => …)` (allocated a `this`-capturing closure every read)
  with a `for` loop over the LRU-ordered list (`Position` hoisted to a local; first match wins, identical to
  `FirstOrDefault`). Added a running `long currentCacheSizeBytes` so the `LimitByRAMUsage` eviction loop no
  longer recomputes `cache.Sum(c => c.Length)` each iteration (was O(n)/iter → O(n²) eviction). The total is
  kept in sync at **every** mutation site: initialised from `precapturedCache` at construction; `-=` before
  each `RemoveAt` in both `LimitBySegmentCount` and `LimitByRAMUsage`; `+=` on `Insert`; net-zero on
  move-to-front (same entry removed+re-added); reset to 0 in `Close()`. Decision logic is byte-identical
  because the running total equals `cache.Sum(...)` at the loop. **Skipped** the optional
  `LinkedList<CacheEntry>` move-to-front (would change the `precapturedCache`/`GetCacheContents` type surface —
  higher risk for a non-bottleneck). **Risk: medium.**
- [x] **P7. `Recurse` (IEnumerable overload) was O(n²)** — `libCommon/Extensions.cs`. `List` used as a queue:
  `RemoveAt(0)` (and the depth-first `InsertRange(0, …)`) shifted every remaining element each step. Swapped to
  `LinkedList<T>` — O(1) `RemoveFirst`, O(1) `AddLast` (breadth-first) and O(1) front-insert preserving order
  (depth-first). Chose `LinkedList` over `Queue<T>` because the method supports both modes and this keeps the
  (currently unused but public) depth-first ordering byte-identical. **Risk: low.**
- [x] **P8. `Ancestors`/`FullPath`/`IsAccessibleToProcess`** — `libDokan/VFS/FileSystemEntry.cs`.
  `Ancestors` now walks the parent chain with a plain `for` loop + `Reverse()` (same `[root … this]` result as
  `Recurse().Reverse().ToList()`, no iterator/closure overhead). `FullPath` computes `Ancestors` once instead
  of twice. `IsAccessibleToProcess` walks the parent chain directly, allocates **no** ancestors list, builds
  `ProcInfo` lazily only when a `RestrictedFolderByPID` ancestor actually exists, and short-circuits on the
  first denial — result is identical (the "any restricted ancestor denies" test is order-independent).
  **Did not cache `FullPath`** (the immutability-after-build precondition isn't verified — left as a per-call
  compute, just cheaper). **Risk: low/medium.**
- [x] **P9. Drop `Synchronized` wrapper in `IndependentStream`** — **RESOLVED 2026-07-10** exactly as
  this entry prescribed, as part of the `SharedStream`/`CreateView` refactor (pattern back-ported from
  the standalone ZstdSeekable package): `IndependentStream` is gone; its replacement (a private view
  minted by `SharedStream.CreateView()`) locks **every** touch of the base stream on one gate —
  including `Length` — and `Position` is a view-local field, so the redundant `Stream.Synchronized`
  layer is dropped with the atomicity concern properly addressed. Call sites no longer see the lock
  object or the wrapper class. Remaining `Stream.Synchronized` uses reviewed and deliberately KEPT:
  `SynchronisedExtractor` (serialises one shared extracted stream across Dokan handles — a different
  job than independent cursors) and `CompressedImage`/`ImageFile` (guard mixed direct-sequential +
  shared access phases on drive-image streams; cheap insurance).
- [skip] **P10. bzip2 `ReadFromChunk` per-read allocations** — `Bzip2StreamSeekable.cs:86-102` news up
  IndependentStream+SubStream+MemoryStream+Multistream+BZip2Stream per read. Inherent to seek-anywhere decode;
  the `CachingStream` above absorbs repeats. **Decision: leave** (note only) unless profiling shows small
  reads slipping past the cache.

**Verification:** full suite. P4 and P8(caching) deserve the most scrutiny — run twice if a flake is suspected.

---

## Design note P1 — behaviour-preserving Folder dictionary

Current semantics to preserve:
- **Storage/enumeration:** `List`, allows name-duplicates (dedup is reference-only via `Contains`), order = insertion.
- **Lookup (`GetEntryFromPath`):** `OrdinalIgnoreCase`, returns the **first** match by insertion order.
- **Enumeration (`FindFiles`):** returns **all** children, including case-colliding siblings.

A naive `Dictionary` keyed `OrdinalIgnoreCase` as the sole store would **silently drop** case-distinct siblings
(`README` vs `readme`) — legal on the Linux ext4/xfs images this tool mounts. So the list stays the source of
truth. A name-keyed *guard* is also wrong: the `Parent` setter re-enters `AddChild`, and with two distinct
same-name siblings a name-keyed guard never matches the re-entrant object → infinite recursion. So the guard
must be **reference-identity** (`HashSet`). Final shape (as implemented):

```csharp
readonly List<FileSystemEntry> children = [];                  // every child, insertion order (enumeration)
readonly HashSet<FileSystemEntry> childrenSet = [];            // reference identity: O(1) de-dup + recursion guard
readonly Dictionary<string, FileSystemEntry> childrenByName    // name -> first-inserted: O(1) lookup
    = new(StringComparer.OrdinalIgnoreCase);

public void AddChild(FileSystemEntry entry)
{
    lock (childrenLock)
    {
        if (!childrenSet.Add(entry)) return;       // already present (incl. the re-entrant Parent-setter call)
        children.Add(entry);                       // keeps EVERYTHING (incl. case-distinct siblings)
        childrenByName.TryAdd(entry.Name, entry);  // keeps the FIRST (matches FirstOrDefault-by-insertion-order)
        entry.Parent = this;
    }
}

public FileSystemEntry? GetChild(string name)
{
    lock (childrenLock) { childrenByName.TryGetValue(name, out var entry); return entry; }
}
```

Behaviour-preserving for both lookup and enumeration, lookup O(1), `AddChild` amortized O(1). `Children`
(enumeration snapshot) unchanged. `GetEntryFromPath` replaces the two `FirstOrDefault` scans with `GetChild`
(`match as Folder` / `match as FileEntry`, branching on type as before).

---

## Batch 5 — Partclone arithmetic content map  **(done 2026-06-18; 10h run surfaced a bug — fixed; legacy path removed)**

Validated `libPartclone` against the partclone 0.3.47 C source (`IMAGE_FORMATS.md`, `src/fuseimg.c`
`read_block_data`, `src/partclone.c` `cnv_blocks_to_bytes` / `get_checksum_count`, `src/bitmap.h`). The offset
math was **correct**; the cost was in *representing* it. The legacy `DeduceContiguousRanges` splits a
`List<ContiguousRange>` at **every checksum strip**, so the map scales with data volume, not fragmentation
(V1: `bpc=1` ⇒ one range *per populated block* — millions; V2: one per `bpc` populated blocks). partclone never
materialises this — it derives the image offset arithmetically. We now do the same.

- [x] **P14. Arithmetic content map (decommission the per-strip list).** New `BitmapContentMap`
  (`libPartclone/BitmapContentMap.cs`) keeps the bitmap as packed 64-bit words + a sparse cumulative-popcount
  (rank) index, and computes the image offset of any position with partclone's own formula:
  `startOfContent + used*blockSize + (used / blocksPerChecksum)*checksumSize`, where `used` = popcount of the
  bitmap before the block (O(1) via the rank index). Read length is clamped arithmetically to the checksum-strip
  boundary and to the populated-run end (a bounded forward scan, word-accelerated). Memory drops from
  O(populatedBlocks/bpc) range objects to ~1 bit/block + a tiny index; **the `*.PartcloneContentMapping.json`
  cache is no longer needed** for this path (building the index from the resident bitmap is a single popcount
  sweep). **Risk: medium** (read data-path).
- [x] **P15. Behaviour-preserving abstraction.** Introduced `IPartcloneContentMap`
  (`Locate(position,count)` + `RestIsAllNullFrom(position)`); `PartcloneStream.Read` is map-agnostic. A
  temporary `PartcloneMapStrategy.{Arithmetic,RangeList}` flag let the 10h run diff the two; **both the flag and
  the `RangeList` path are now removed** (see P18). **Risk: low.**
- [x] **P16. Correctness fix: `blocksPerChecksum == 0` divide-by-zero.** No-checksum V2 images
  (`CSM_NONE`) carry `blocks_per_checksum = 0`; this matches partclone's `get_checksum_count` guard. **Risk: none.**
- [x] **P17. Robustness: V2 bitmap-size `(int)` overflow.** `(int)(TotalBlocks + 7) / 8` cast before the
  divide (overflow above ~2³¹ blocks); now divides first. **Risk: none.**
- [x] **P18. Legacy path removed.** Deleted `DeduceContiguousRanges`, `ContiguousRange`,
  `ContiguousRangeComparer`, `RangeListContentMap`, the `PartcloneMapStrategy` flag, and the
  `PartcloneContentMapping` JSON cache (`IPartcloneCache` and its `PartitionCache`/threading). `ByteRange` was
  kept (reused by a test util). `BitmapContentMap` is now the sole content map; no on-disk map cache exists.
- [x] **P19. Bug fix from the 10h run: device larger than the bitmap.** The 10h suite failed 3 `ImageFileTests`
  (pb-devops1 sda1, gz/zst). Verified against ground truth by SSH'ing to a Clonezilla box and running stock
  `partclone.restore` on the same sources: the test's expected MD5s were **correct** — the arithmetic map was
  wrong. Root cause: the image's `device_size` (575,668,224 = 140,544 blocks) is **one block larger than
  `totalBlocks` (140,543)** — the NTFS backup-sector area past the FS. Those trailing blocks aren't in the
  bitmap; the legacy map zero-filled them to device size but `BitmapContentMap.Locate` returned `Length = 0`
  there (its `CountRun` caps at `totalBlocks`), so the stream hit EOF a block early → different MD5. Fixed:
  blocks at/beyond `totalBlocks` are treated as implicitly empty and zero-filled to device size. The synthetic
  tests missed it because they all used `deviceSize == totalBlocks*blockSize`; added `V2_DeviceLargerThanBitmap`
  (and the partial-trailing-block case) covering it for V1/V2. Re-verified the arithmetic output's MD5 equals
  the canonical `partclone.restore` (`d9a64d…`) on the real image. **Minor still open:** V2 endianess marker is
  unchecked (x86 only); bitmap CRC / `BiTmAgIc` not validated; the resident raw `Bitmap` is still kept (could be
  dropped now that `BitmapContentMap` owns the packed words).

- [x] **P20. Demote the "Building partclone content map" log to Debug.** It was `Log.Information` (so it hit the
  console + rolling log file) to reassure during the old slow per-strip build. The arithmetic map makes it
  near-instant, so it's now `Log.Debug` (`libPartclone/PartcloneImageInfo.cs`) — off the console/log file, still a
  breadcrumb in the debugger-output sink. **Risk: none** (logging only).

**Verification:** `clonezilla-util_tests/Partclone/PartcloneContentMapTests.cs` synthesises V1, V2
(checksummed), and V2 (no-checksum) images in memory and asserts the restored bytes equal **ground truth**
across many chunk sizes and 1500 random seeks per scenario, plus edge patterns (all-empty, all-populated,
alternating, leading/trailing empty, single block), a partial last block, runs spanning many strips, **device
larger than the bitmap** (the P19 regression), and the sparse early-stop. All 7 pass. End-to-end: the next full
suite run should pass the 3 previously-failing `ImageFileTests` (the arithmetic output now matches the canonical
`partclone.restore` MD5 on the real image; confirmed locally).

---

## Notes / decisions

- Batches are independent; reorder freely. Value-first order is B1 > B2 > B3 > B4.
- B2/B3 are mostly "free" (byte-identical output) — if you want a low-anxiety first run to validate the
  batch+test workflow, start there instead of B1.
- P9 and P10 are intentionally **not** being done (concurrency risk / inherent cost) — see above.
- Correctness-review history is in `CODE_REVIEW_PLAN.md`; this file is performance-only.

## Regression bisect — wall-clock cost of the D1–D6 Dokan mods  (opened 2026-06-29)

The D1–D6 robustness fixes (`CODE_REVIEW_PLAN.md`, the "5-layer optimisation") fixed the
`0x800705AA` errors but introduced a **~23% wall-clock regression** in the full suite. Confirmed
**structural (code, not environment)**: it reproduced across two independent clean machines.

### Two separate problems, now disentangled
- **Machine-unusability during/after the suite = paging.** On the old setup, memory pressure paged
  onto a slow disk (`E:`) → thrash. **Relieved by reinstalling on a larger/faster SSD** (2026-06-29
  run): machine responsive, every paging-sensitive bucket recovered to baseline (`Mount.AsFiles`
  308.9→270.3, `ext4_lvm` 73.7→**53.2**≈base, `Sparse`, `dd`). The underlying *memory growth* is
  likely still present (prime suspect **D4** — `FileEntry.memoryMappedStream ??= GetStream()` is
  cached and **never disposed**, pinning the ¼-RAM cache per mount); now masked by fast paging, not
  fixed. Track separately.
- **Wall-clock slowdown = code.** Reproduced on a fresh Win11/SSD; a faster disk did **not** touch it.

### Regression baseline (minutes) — pre-mods vs after D1–D6

| Bucket | **Baseline** 06-22 (pre-D1–D6) | After D1–D6, clean: 06-27 reboot / **06-29 reformat** |
|---|---|---|
| **TOTAL** | **618.4** | 746.1 / **759.2** |
| ListContents › **LargeDriveImages** | 229.2 | 339.9 / **401.3** |
| ·· **Xz** (single 16.8 GB block) | **142.4** | 240.6 / **270.7** |
| ·· **Gz** (gztool + native pool) | **34.8** | 33.0 / **62.4** |
| ·· **Zst** | **31.0** | 48.0 / **48.8** |
| ListContents › LargeClonezillaPartitions | 95.3 | 65.2 / 65.2 *(faster)* |
| Mount.AsFiles | 263.4 | 308.9 / 270.3 *(≈base on SSD)* |

(Full numbers: `clonezilla-util local/unit test stats/2026-06-22 … decomission 7z.exe.txt` and
`… 2026-06-29 … reformat to windows 11.txt`.)

### Diagnosis
The entire +141 min is **`ListContents.LargeDriveImages`** (+172, partly offset by −30 on
partitions). Everything else is at/below baseline. The slowdown is **format-independent** (xz, gz,
*and* zst all regress on the drive image, while the same codecs on partitions/mounts don't) and lands
on the **read-heaviest** workload (full tree-walk of a 16.8 GB multi-partition image = the most reads
of any test). That points at the **shared per-read path → prime suspect D3** (`fab6539`,
`PooledNativeItemStream.Read` re-opens + re-seeks the native item on **every** `Read`). Gz doubling
fits: gz drive-image listing now pays gztool-per-read **and** D3-per-read-reopen, stacked.

### Bisect — fast proxy is `ListContents.LargeDriveImages.Zst` (baseline **31.0**, regressed **48.8**, on the exact regressed path, ~5× quicker than Xz)

Cumulative peel from the regressed state; when Zst returns to ~31 we've found all contributors.

| Experiment | State | Zst (min) | Δ vs regressed (48.8) | Verdict |
|---|---|---|---|---|
| — | baseline (pre-D1–D6) | 31.0 | — | target |
| — | all D1–D6 (06-29) | 48.8 | — | regressed |
| **1** | **D3 removed** | **43.4** | **−5.4** | **D3 = 5.4 min (partial)** |
| **2** | **D3 + D6 removed** | **35.1** | **−13.7** | **D6 = 8.3 min (largest); D3+D6 = ~77%** |
| — | residual vs baseline | 35.1 vs 31.0 | ~4 min left in D1/D2/D4/D5 (likely format-independent / partly cross-machine noise) |

- [x] **Experiment 1 — D3 removed (`fab6539`), D4–D6 kept.** Result **43.4 min** — recovered ~5.4 of
  the 17.8 min regression (~30%). D3 (`PooledNativeItemStream` per-read reopen) is a **real but partial**
  contributor; ~12 min of regression remains elsewhere. Keep D3 reverted; design its proper fix later
  (Batch 6 (D) item-stream reuse with worker-affinity — reuse the open item across consecutive reads
  instead of per-read reopen, without going back to "handle holds a worker").
- [x] **Experiment 2 — additionally remove D6 (`618fcfd`).** Result **35.1 min** — D6's marginal cost is
  **8.3 min, the largest single contributor**. Confirmed cause: D6 wraps every `ReadFile` in
  `using var watchdog = StartTimeoutWatchdog(info)`, which **allocates + schedules + disposes a
  `System.Threading.Timer` on every read** (even fast reads that never tick). A drive-image tree-walk
  issues millions of tiny metadata reads → millions of timer churns = read-count-proportional overhead.
- [skip] **Chase the last ~4 min (D1/D2/D4/D5).** Small and format-independent; partly cross-machine
  noise. Not worth more 35-min runs — the full validation run after the proper fixes will surface it if
  real (and on the heavy-decode Xz test it'd show up amplified).

### Conclusion & the two fixes to build

**Culprits: D6 (timer-per-read, 8.3 min) + D3 (item reopen-per-read, 5.4 min) = ~77% of the regression.**
Both are read-count-proportional, which is why the regression concentrated in `ListContents.LargeDriveImages`
(the read-heaviest workload). **Note the format amplification:** on the Zst proxy D3 was only 5.4 min because
zst decode is cheap, but D3 re-*decodes* on every reopen — so on the headline **Xz** test (single 16.8 GB
block, expensive decode) D3's share is far larger and is likely the dominant part of that +128 min. So both
fixes matter; D3 most for xz, D6 broadly.

The reverts are **diagnostic only** — we must keep the robustness D3/D6 bought (D3: opens don't block a
CreateFile into `0x800705AA`; D6: genuinely-slow fragmented reads don't trip the 20s Dokan timeout). Restore
HEAD and reimplement both properly:

- [x] **Fix D6 — shared watchdog instead of a per-read Timer.** (`libDokan/VFS/DokanVFS.cs`, built clean
  2026-06-29.) Replaced the per-`ReadFile` `new Timer(...)` with **one free-running static `Timer`** (5s
  interval) that walks a `ConcurrentDictionary<long,(IDokanFileInfo,StartedTick)>` of in-flight reads and
  `TryResetTimeout`s each still-running one (until the 10-min runaway cap). `StartTimeoutWatchdog` now just
  inserts into that dictionary and returns a `readonly struct WatchdogRegistration` whose `Dispose()` removes
  it — so `using var` costs no allocation and the fast path pays only a dictionary insert/remove (no Timer
  alloc, no global timer-queue lock). A fast read completes between ticks and is never touched; only reads
  still alive at a 5s boundary (the genuinely-slow ones) get extended — same protection D6 gave, without the
  churn. **Result: Zst 48.8 → 34.9 min** (with D3 still present) — recovered **13.9 min**, *more* than D6's
  isolated 8.3. And 34.9 ≈ 35.1 (the D3+D6-both-removed state), i.e. **with the timer churn gone, D3 now
  costs ~0 on this test.** Read: the per-read Timer wasn't only its own 8.3 min — the timer-queue-lock + GC
  pressure it generated *also* inflated D3's reopens; remove it and D3's reopens get cheap (CachingStream
  absorbs them). So D3's "5.4 min" was largely an interaction artifact of D6.
  **Full-suite confirmation (06-29, 693 min, 74/74 green):** −66 min total; Gz & Zst back to baseline; slow-read
  protection intact (no `0x800705AA`; `ext4_lvm`/`dd`/Ubuntu-FS all pass). **Keep this fix.**

> **Full-suite run (06-29, D6-fix, 693 min, 74/74 green):** Gz (62.4→40.4) and Zst (48.8→33.9) **recovered
> to baseline**, but **Xz is still +105.7 min** (270.7→248.1 vs 142.4 baseline).
>
> **Correction (was: "D3 is the xz culprit" — wrong).** Two facts rule D3 out: (1) with D6 fixed, D3 costs
> ~0 even on zst (proxy: 34.9 *with* D3 vs 35.1 without); (2) xz and zst drive images share the **same**
> read path — both have no seekable decoder, both serve from the same on-disk `cache.train` (zstd) — so
> D3's reopen/reseek is format-agnostic. If D3 ≈ 0 on zst it's ≈ 0 on xz. **The D3 fix would not move the
> xz residual.**
>
> **What the xz residual is:** format-amplified (only the slow-decode xz; ~0 on zst/gz), i.e. a mod adding
> *decode* work, not per-read overhead. Not statically pin-pointable (the train-cache path makes every
> read-path mod look format-neutral). **Lead guess D4** — its never-disposed cached mmap stream may be a
> *second* long-lived decode stream over the xz source (≈ doubles the one-time 16.8 GB xz decode; ~free on
> fast-decoding zst). Unproven — **bisect it (below).**
- [x] **Experiment 3 — D4 reverted (on the committed D6-fix), two full-suite runs.**
  - 07-01: 74/74, 680.9 min; **Xz 234.4** — no recovery → **D4 exonerated** (restored to HEAD 07-03).
  - 07-03: 683.8 min; **Xz FAILED at 254.2 min** — a *crash*, not a perf/correctness signal (see
    Investigation I1 below). All other 73 passed.

### Status (2026-07-03): xz residual — leading hypothesis ENVIRONMENTAL; Experiment 4 decides it.

> **Operator input (2026-07-03):** no hardware change — the 5×8 GB @2133 config predates the regression
> (it ran 287 MB/s on that same RAM on 06-22), so the RAM-config theory below is **dead**. The operator
> suspects the 06-23/24 code drop and wants a revert-to-confirm. **Experiment 4 (below) is that test in a
> single run.** Remaining environment suspects if Exp 4 exonerates the code: Defender real-time on the
> fresh install (confirmed ON, exclusions unknown; the build streams 19 GB reads from E: + tens of GB of
> writes to R: past it) and Win11 EcoQoS/background power-throttling of the long-running console exe.

The 07-03 log revealed the Xz test is ~99.8% **train-cache build** (`E: FileStream → SharpCompress
XZStream → train-zstd → R:`) — a path containing **no Dokan code**, so no D-mod could ever have caused
it. Chased the two remaining code suspects to ground the same day:

1. **Local SharpCompress package is a proper RELEASE build** (DebuggableAttribute flags 0x2 — optimized;
   verified by metadata inspection of the nupkg's net10.0 DLL). Debug-pack theory dead.
2. **SharpCompress 0.37.0 vs 0.49.1-localbzip2patch decode the actual test file at identical speed**
   (micro-bench on the real `…sda.img.xz`, 60 s measured legs: **35.2 vs 34.5 MB/s** — noise). Upstream-
   regression theory dead. The 06-23 package migration is exonerated.

What remains is the machine. Hard evidence from the app's own logs (train build of the SAME image,
cache hash `f7197852…`):

| Run (install) | Build duration | Tail rate (identical 1.7→2.0 TB range) |
|---|---|---|
| 06-21 ×2, 06-22 (old install) | 160.5 / 156.4 / **148.4 min** | **287 MB/s** (06-22) |
| 07-01 / 07-02→03 (fresh Win11/SSD) | ~230 / **253.6 min** | **155 MB/s** (07-03) |

**1.85× slower on identical work with a speed-identical decoder = environment.** Crucially the slowdown
**pre-dates the reformat** (06-27 old-install post-reboot run: Xz 240.6) and **survived the OS reinstall**
→ the cause is at BIOS/hardware level, not Windows. Measured on 2026-07-03: CPU = i7-5820K running a
normal 3.4 GHz under this exact load (no throttle, no E-cores to mis-schedule); **RAM = 5 × 8 GB mixed
vendors (Micron/Crucial/Team Group) all at JEDEC 2133** — an asymmetric 4+1 population on a quad-channel
X99 board. LZMA2 decode is memory-latency/bandwidth-bound, exactly the workload such a config punishes.
Power plan is Balanced (worth setting High performance, but that alone won't explain 1.85×).

**Open question only the operator can answer:** was a RAM stick added (or CMOS/XMP reset) around
2026-06-23→26 — i.e. during the memory-exhaustion firefighting? That would explain the timing, the
persistence across reformat, and the 5-stick mix. Verify BIOS: XMP profile, channel population per the
board manual. Cheap re-validation after any change: the scratchpad `benchxz` micro-bench (~2 min),
target ≥ ~55 MB/s cold on the same file — no 4 h test needed.

**Bisect scorecard so far:** D6 per-read Timer = real, fixed & committed (`e7f414d`). D3 = interaction
artifact of D6, ≈0 once D6 fixed. D4 = exonerated (Exp 3). D1/D2/D5 = untested individually, but the
build path contains no Dokan code. Zst proxy at parity (33.9–34.2 vs 31.0 baseline). Suite total
680–693 vs 618 baseline ≈ the xz tax (+~90–105) minus SSD gains elsewhere.

- [x] **Experiment 4 — THE decisive run: baseline binary (`60c4f26`, the 06-22 build) on today's
  machine, Xz test only (~4 h).** **Ran as a full suite 07-08→09 (74/74, 737 min): Xz = 194 min** —
  between the two predictions → split verdict, decomposed below. → **RESOLVED, see next section.** Tests the ENTIRE 06-23/24 delta (D1–D6, D6-fix, SharpCompress
  0.37→0.49.1-patch, gztool 1.8.2) at once — the single-run equivalent of "revert all the optimisations".
  Do after the 07-03 full-suite re-run (rclone off) completes.
  - **STAGED 2026-07-04:** baseline worktree created and published (smoke-tested OK) to
    `C:\Users\Smith\Desktop\dev\cs\clonezilla-util-baseline\publish-baseline\clonezilla-util.exe`.
    To run: copy that exe over `R:\Temp\clonezilla-util release\clonezilla-util.exe`, run
    `ListContents.LargeDriveImages.Xz` (playlist `bisect-xz-only.playlist`, ~2.5–4.5 h). Afterwards
    redeploy the current build (VS publish) and `git worktree remove ..\clonezilla-util-baseline`.
    (Baseline pins official SharpCompress 0.37.0 from nuget.org — no local feed involved.)
  - Current-code Xz plateau for comparison: **248.1 / 234.4 / 265.1 / 265.4** (mean ~253) across four
    D6-fix full-suite runs (D4 in, out, in, in — no effect; pure run noise). The 07-07 run (74/74,
    741.2 min total) was the current build, NOT this experiment (deployed-exe hash ≠ baseline exe);
    its total and several small tests carry daytime-contention noise (e.g. Zst drive image 67.5 vs
    34–49 typical) — Xz still landed on-plateau.
  - **~148–160 min → the code/package delta IS the cause** → commit-bisect the 06-23/24 window
    (d93444f pkg swap → 21757dd gztool → D1..D6) with Xz-only runs.
  - **~230–255 min → code fully exonerated, environment confirmed** → then toggle environment suspects
    cheaply via the `benchxz` micro-bench (2 min/leg, no 4 h runs): (1) add Defender exclusions for
    `E:\clonezilla-util-test resources` + `R:\Temp` (+ the exe) and re-bench; (2)
    `powercfg /powerthrottling disable /path "R:\Temp\clonezilla-util release\clonezilla-util.exe"` and/or
    High-performance power plan; (3) re-bench cold vs warm (cold 35.2 vs warm 46.7 MB/s on 07-03 already
    hints the E:-read path carries a ~33% tax).

- [ ] **Fix D3 — item-stream reuse with worker affinity (= Batch 6 (D)).** **Deprioritised** — D3 ≈ 0 once
  D6 is fixed (see correction above), so this is a latent cleanup for very-high-concurrency mounts, **not**
  the xz regression fix. Revisit only if a future profile shows reopen cost. Don't build it speculatively.

### RESOLVED (2026-07-09): xz regression root cause = the local SharpCompress pack was built from
### upstream MASTER, not the 0.49.1 tag. Fixed by a tag-based repack (`0.49.1-localbzip2patch2`).

Experiment 4 (baseline binary on today's machine) split the regression, and the identical-work
**tail-rate** decomposition closed it exactly: package 1.60× × machine 1.16× = 1.85× observed.
- Old install + 0.37 binary: 287 MB/s | new install + 0.37 binary: 239–248 | new install + 0.49-pack: 155.

Null-region micro-bench (8 GB-of-zeros xz; head-region rates are EQUAL across all — the regression is
sparse/high-ratio-region-specific, which is why the earlier head-only bench wrongly exonerated the pkg):

| Decoder | MB/s |
|---|---|
| SharpCompress 0.37.0 | 275 |
| **0.49.1-localbzip2patch (old pack, master-based)** | **159** |
| 0.49.1 OFFICIAL (nuget.org) | 283 |
| **0.49.1-localbzip2patch2 (new pack, tag-based)** | **283** |
| XZ.NET native liblzma | 325 |

**Official 0.49.1 was never slow.** The 06-23 pack was accidentally built from the fork's master
(= 0.49.1 + ~3.5 weeks of upstream dev commits, one of which regressed the XZ sparse path ~1.7×).
Meanwhile **upstream merged our bzip2 PR (#1358) on 2026-06-23** but hasn't released it. Fix applied
2026-07-09: downloaded the 0.49.1 tag source, applied the merged PR's src hunks (tests excluded —
file moved on master), packed as `0.49.1-localbzip2patch2`, added to the local feed, bumped both
csprojs; solution builds; bench confirms 283 MB/s (= official).

- **CONFIRMED by the 07-10 full-suite run (74/74): Xz = 192.8 min** (was 248–265 with the master-based
  pack; baseline binary got 194) — the package component is fully recovered. The remaining ~35–50 over
  the old 142–160 is the ~1.16× machine component + night-to-night variance, worth a
  Defender-exclusion / power-plan experiment via the 2-min bench some idle day. NB the rest of the
  07-10 run was heavily contended (total 823.6 — worst ever; Gz drive image 110.2 vs ~38 typical,
  Sparse 16.9 vs ~6, Train/partitions elevated) with a time-of-day signature (evening+morning tests
  slow, middle-of-night tests fast — Xz ran overnight and STILL dropped 70+ min); none of the slow
  buckets touch SharpCompress, so the contention is environmental, not the repack. A clean-night
  total should land ≈ **620–640**, i.e. back at the 618.4 pre-regression baseline.
- **Optional follow-ups:** (a) switch xz decode to XZ.NET native (`xzDecompressor.cs` /
  `Decompressor.cs`) for a further ~15% and immunity to SharpCompress churn — deliberately NOT done
  together with the repack, to keep run attribution clean; (b) report the master XZ sparse-path
  regression upstream (first re-bench current master — it may already be fixed); (c) when upstream
  ships a release containing PR #1358: switch both csprojs to the official version, delete
  `nuget.config` + the `local-nuget` feed.

## Investigation I1 — intermittent crash: FileNotFound on our own internal mount  (opened 2026-07-03)

`ListContents.LargeDriveImages.Xz` failed on 07-03 because **the exe died on an unhandled exception**,
first occurrence ever (same binary passed 07-01). From `clonezilla-util_tests\bin\Debug\net10.0\logs\
clonezilla-util-20260703.log` line 99:

- 00:34:26 `Successfully cached to …\f7197852…\cache.train` (the 4 h xz train build completes normally)
- 00:34:55 `[FTL] Unhandled exception System.IO.FileNotFoundException: Could not find file
  'Z:\uktluojm\e21rbio4\h4oaywly'` at `CompressedImage..ctor` (`CompressedImage.cs:26` `File.OpenRead`)
  ← `ImageFile..ctor` ← `PartitionContainer.FromPath` ← `Program.ListContents` (`Program.cs:177`).
  Process dies → no listing printed → test asserts "Could not find …ReAgent.xml".

That path is **our own virtual file**: the ctor loop publishes the just-built decompressed stream as a
`StreamBackedFileEntry` on the internal Dokan VFS (Z:), then immediately `File.OpenRead`s it (peeling
the next layer). The volume answered (FileNotFound, not PathNotFound → the mount was alive); **our own
`CreateFile` said the entry doesn't exist ~29 s after creating it.**

**CONFIRMED by the 07-04 full-suite run (rclone off): 74/74 green, Xz passed (265.1 min).** The crash
did not recur once rclone was absent — and that run's binary predates the hardening below, so the pass
isolates the rclone variable itself.

**Root cause (operator-identified, 2026-07-03): drive-letter collision with rclone.** rclone was
mounted to Z: in another session during that run. Our mount logic picks "the next available letter"
and then just **waits for the letter to appear** — it never verifies the volume that appeared is OURS.
If a foreign volume owns (or grabs) the letter, we adopt it: the volume answers, our random path
doesn't exist on it → FileNotFound → unhandled → process death. Explains the intermittency (only when
rclone/another mounter is active) and why the volume was "alive". The 07-03 full-suite re-run with
rclone off doubles as confirmation (Xz should pass).

**Hardening implemented 2026-07-03; VALIDATED by the 07-07 full-suite run** (74/74 green on a binary
containing it — every ListContents/mount test exercised `TryMount` + sentinel verification with zero
regressions; ready to commit) — verification, not retry (a retry against a foreign volume would spin
forever):
1. **Sentinel-based volume-identity verification** (`OnDemandVFS.TryMount`): each mount attempt plants
   a uniquely-named `UnlistedFolder` (`clonezilla-util-sentinel-<guid>`) in the fresh `RootFolder`,
   mounts, waits for the letter, then probes the sentinel *through the mounted letter* (brief 2 s
   retry for settling). Only our own live VFS instance can answer → a squatter (rclone/subst/network
   mapping) is detected deterministically. On mismatch: WARN naming the foreign volume's label/format,
   tear the attempt down (signal ends the mount task → disposes the DokanInstance), and
   - **auto-chosen letter** → fall back: pick the next free letter **excluding letters already tried**
     (`GetAvailableDriveLetter(excluding:)` — squatted letters can look free to the scan), up to 3
     attempts, then `Log.Fatal` + exit;
   - **user-chosen letter (-m)** → no silent substitution: `Log.Fatal` + exit naming the squatter.
2. **Bounded mount wait**: `WaitForFolderToExist` gained an optional timeout (30 s here) — previously
   a failed Dokan mount left the app spinning forever on `while(true)`.
3. The two mount verbs now log + open Explorer at the **actual** mounted letter
   (`RootFolder.MountPoint`), which can differ from the tentative one after a fallback.
   Files: `libClonezilla/VFS/OnDemandVFS.cs`, `libClonezilla/Utility.cs`, `libDokan/Utility.cs`,
   `clonezilla-util/Program.cs` (4 call sites).

**Validation:** after both fixes, full suite — expect `ListContents.LargeDriveImages` (esp. Xz) back to ~baseline.

- **Restore everything to HEAD:** `git checkout HEAD -- libClonezilla/Extractors/ libDokan/VFS/DokanVFS.cs`

## Investigation I0 — gz seekable decode spawns gztool per read  (flagged 2026-06-25, **DONE 2026-07-10** — awaiting 10h suite)

**Implemented option 1: in-process zran reads using gztool's existing index.** gztool remains the
index *builder* (unchanged, reliable); the per-read `gztool.exe` subprocess is gone. Design:

- `libGZip/GztoolIndex.cs` — parses gztool's binary `.gzi` (v0/v1, big-endian; format from gztool
  v1.8.2's `serialize_index_to_file()`). Windows (≤32 KB each, zlib-compressed in the file) are
  loaded **lazily by file offset** — a TB-scale image has 100k+ points; materialising every window
  would cost GBs. Also replaces the `gztool -ll` text-scrape (`GetIndexContent`) as the mapping
  source (that path remains as fallback).
- `libGZip/Vendored/SharpZipLib/` — vendored SharpZipLib v1.4.2 inflater (MIT, provenance in its
  README.md) with two small additions: `StreamManipulator.PrimeBits()` (zlib `inflatePrime`
  equivalent) and `Inflater.PrimeForResume()` (raw-mode `inflateSetDictionary` + prime). Needed
  because **no BCL/NuGet inflater can resume mid-stream**: DeflateStream exposes neither primitive
  (and a bit-shifted input stream is NOT a valid substitute — stored blocks re-align to the original
  byte grid, discovered the hard way), and ZLibDotNet 0.1.1 (right API) **mis-decodes real DEFLATE
  streams** (`invalid code -- missing end-of-block` mid-stream; worth reporting upstream).
- `libGZip/ZranInflate.cs` — the zran pump: position at `in` (or `in-1` + prime the split bits),
  preload the window, inflate directly into the caller's buffer (discard-skip for mid-chunk starts).
- `GZipStreamSeekable` — mappings built from the parsed points; `ReadFromChunk` serves in-process
  first and **falls back to the old gztool-subprocess path automatically** (unparseable index at
  ctor → whole stream falls back; per-read decode failure/zero bytes, e.g. a multi-member gzip whose
  member ends inside a span → that read falls back). The brittle `bytesRead != bytesToRead` hard-fail
  (jboss xsd copy failure; ext4_lvm test hang) is out of the read path entirely.

**Verification (scratchpad `zranproto` harness):** synthetic 64 MB gz (text/random/zeros/pattern
regions — random regions force stored blocks) indexed by the repo's gztool: parsed points match
`gztool -ll` 7/7 exactly; per-point, block-span, point-edge and 300 random reads through the real
`GZipStreamSeekable` all **byte-perfect vs reference**, including 10.7 MB decodes crossing stored
blocks from bits≠0 points. **Production data:** the real `sda2.ntfs-ptcl-img.gz` index (4,317
points, 45.4 GB uncompressed, bits 0-7 all ~430×): 12 random cross-point checks
(direct-resume vs decode-through-from-previous-point) byte-identical; ~54 MB/s decompressed per
cold ~10 MB span in-process. Full 74-test suite still pending (the gate).

Original notes:

The gz seekable path is **not in-process**: `SeekableDecompressingStream.ReadFromChunk` (for gz, via
`GZipStreamSeekable`) shells out to an **external `gztool` process per cache-miss read**
(`ProcessUtility.ExecuteProcess`). gztool itself has been reliable, but spawning a process per read is a
problem on two fronts:

- **Throughput:** a process spawn per cache-miss chunk is very likely a dominant part of the ~1-4 MB/s
  baseline and the fragmented-file slowness (scattered reads = cache misses = a `gztool` spawn each). Since
  gz is the common format, this distorts the whole Batch 6 baseline — measure/fix it **first**.
- **Reliability under memory pressure:** a copy run failed on `\sda2\keycloak-15.0.2\docs\schema\
  jboss-as-logging_1_5.xsd` when, under exhaustion (free had hit ~409 MB), the `gztool` child couldn't run
  and returned short output → `ProcessUtility.ExecuteProcess` threw `bytesRead != bytesToRead` →
  `CachingStream` → native 7z `Read` callback returned `E_FAIL (0x80004005)` → `SevenZip_ItemRead` failed →
  `IOException` → D1 caught it → `ReadFile` returned `Error` → copy failed on that one file. (D1 kept the VFS
  alive; only that file failed.)

**Goal:** eliminate the per-read process spawn while keeping gztool's reliable index. Options to weigh:
1. **In-process gzip seekable decoder using the existing gztool index** (preferred). The `.gzi` already holds
   the DEFLATE checkpoint windows (~32 KB each); decode in-process (zlib/DEFLATE + window-prefix resume),
   exactly the zran technique — and the same shape as Batch 7's zstd plan. Removes the spawn entirely and
   makes gz fully in-memory. Keep using gztool (or our own builder) to *create* the index.
2. **One long-lived `gztool` process** fed read requests (avoid per-read spawn) — depends on gztool's CLI
   supporting a request/response mode; less clean.
3. **Coalesce / readahead** so there are fewer, larger gztool invocations — reduces spawn count, doesn't
   eliminate it.

Also note: `ProcessUtility.ExecuteProcess`'s strict `bytesRead != bytesToRead` throw is a brittle hard-fail
point (no retry/grace) — revisit as part of whichever option is chosen.

## Batch 6 — Mount read throughput & memory (in-memory only)  (flagged 2026-06-24, NOT yet done)

> **Refer to this as "Batch 6" (the mount-throughput batch).**

**HARD CONSTRAINT — everything stays in memory.** No large temp files, no materialising decompressed
partition/drive data to disk. Drive images can be larger than any local disk; doing all decompression and
serving in memory (the seekable-stream machinery over traditionally non-seekable formats) is the entire
reason the project exists. Any "decompress once to scratch" idea is **out**.

Surfaced by a full Explorer copy of the gz image `2022-07-17-16-img_pb-devops1` (558,857 files): most files
copy, but it trends toward memory exhaustion and a few fragmented files are pathologically slow. The D6
`TryResetTimeout` watchdog (`CODE_REVIEW_PLAN.md` D6) stops the slow reads erroring, but makes them
*complete*, not *fast*. This batch is about speed + memory.

### Measured baseline (gz, pb-devops1 sda2, reads via the mount; from the diagnostic run)
- "Normal" files (DLLs/exes, 1-26 MB): **~1-4 MB/s** — works, but slow for gz.
- Heavily-fragmented files (logs / `Temp` / WU `SoftwareDistribution\Download` blobs): **~0.1-0.6 MB/s**,
  deterministically; a warm re-read of an 863 KB log was *still* 0.1 MB/s (warming barely helps → structural).
- Memory (per the `[diag]` snapshots): `decompCache` climbs steadily (2.4→4.9 GB and rising); `gcHeap`
  saw-tooths violently (≈4→11.5 GB, ~4.5 GB collected per 2 s step) with `gcFrag` spiking to **2.2 GB**;
  process private peaks **~14 GB**; system free dips to **~1.2 GB**. Two memory pressures, not one.
- Concurrency = 1 (`7zHandlersInUse=1`): Explorer copies serially, so the worker pool is not the bottleneck.
- `pagefile.sys` (and `hiberfil.sys`/`swapfile.sys`) are being copied — huge, slow, almost always useless.

### Two root problems
1. **Cache budget**: the layer-4 `CachingStream` is sized at ¼ system RAM **per partition**; with N mounted
   partitions the total is N×¼ RAM → exhaustion (≈¾ RAM on a 3-partition mount) → `0x800705AA`.
2. **Decompress-and-discard on scattered reads**: a fragmented file scatters NTFS clusters across the whole
   partition → scattered seeks into the gz stream → each seek decompresses from the nearest gztool checkpoint
   and throws the skipped part away. This is *both* the slowness *and* the transient-allocation/LOH-frag churn.

### Stream stack & where each optimisation slots in
```
[10] DokanVFS.ReadFile
[ 9] FileEntryStream / FileEntry mmap cache
[ 8] PooledNativeItemStream ───────────────┐ (D) item-stream reuse
[ 7] NativeItemStream (7-Zip NTFS handler) ┘
        ── borrows a pool worker per Read ──
[ 6] IndependentStream (shared streamLock)
[ 5] PartcloneStream (bitmap → raw NTFS image = FullPartitionImage)
[ 4] CachingStream (¼ RAM / partition) ──── (A) global budget   (C) in-memory readahead
[ 3] GZipStreamSeekable (gztool index) ──── (B) denser checkpoints   (E) pooled decompress buffers
[ 2] Multistream (concat split .gz volumes)
[ 1] FileStream × N (sda2.…ptcl-img.gz.aa/.ab/…)
```

- [x] **(A) Global cache budget** — **DONE 2026-07-10 (awaiting suite).** `CachingStream` now tracks a
  process-wide count of live `LimitByRAMUsage` instances; each caps itself at
  `CacheLimitValue ÷ liveCount` (all callers pass ¼ RAM, so the TOTAL stays ≤ ¼ RAM regardless of
  partition count — was N×¼). No cross-instance locking (each instance evicts only its own LRU);
  never-Close()d instances err toward LESS memory. Fixes root #1 (exhaustion → paging → 0x800705AA /
  machine unusability). Tension stands: shrinks each cache → more misses → leans on (B)/(E).
- [x] **(B) Denser gztool checkpoints** — **DONE 2026-07-10 (awaiting suite).** Index span 10 → 4 MiB
  (`gztool -s 4` in `GZipStreamSeekable`): average cold-seek discard ~5 MB → ~2 MB (~2.5× less decode
  per scattered read), compounding with I0's in-process reads. Cost: ~2.5× bigger on-disk index
  (68 MB → ~180 MB for the 45 GB sda2); windows load lazily so resident memory unaffected. Existing
  cached indexes keep their density and stay valid. Verified via the zranproto harness at -s 4.
- **(C) In-memory readahead** — layer 4 (or a thin layer between 3 and 4). On a miss, decompress a larger
  forward span and cache it. Helps **sequential** access; limited for scattered/fragmented. **No disk
  materialisation** — the rejected "decompress-once-to-scratch" variant is explicitly out (see HARD CONSTRAINT).
- **(D) Item-stream reuse** — layers 7-8. Stop re-opening the `IInArchiveGetStream` (`NativeItemStream`) on
  every `ReadFile`; cache it per handle and reuse for consecutive reads, with light worker affinity that
  yields under contention (keeps D3's unbounded opens). **Fixes the ~1-4 MB/s baseline for normal files**
  (per-read native + cluster-run-resolution overhead). Tension: a held item stream pulls back toward
  "handle holds a worker" (what D3 removed) — the affinity-with-yield hybrid is the way.
- [x] **(E) Pooled decompress buffers** — **DONE 2026-07-10 (awaiting suite).** `CachingStream` cache
  segments (a fresh multi-MB `byte[]` per cache miss, dropped to GC on eviction — the main LOH-churn
  source behind the ~2 GB `gcFrag` spikes) now rent from `Buffers.BufferPool` and return on
  eviction/Close (also on the serve-without-caching path). `CacheEntry.Content` may exceed `Length`
  (pool over-allocation); all access is Start/End-bounded. The short-read `Array.Resize` copy is gone
  too. I0's `ZranInflate` was born pooled. (gz/bzip2 per-read wrapper allocations = P10, still
  deliberately left.)
- [ ] **Free win (not a stream change):** skip `pagefile.sys` / `hiberfil.sys` / `swapfile.sys` (huge,
  useless). **Deliberately NOT implemented as a silent default (2026-07-10):** hiding real files from
  the mounted view trades data fidelity for copy speed — wrong call for a tool people may use
  forensically. If wanted, do it as an opt-in CLI flag (e.g. `--exclude-system-files`); needs a
  product decision.

### Grouping / sequencing
- **Memory-safety (stops the errors), near-term:** **(A)** — bounded, low-risk. Plus the pagefile exclusion.
- **Speed + allocation churn on fragmented files (the deep issue):** **(B)** + **(E)** (high-leverage pair);
  **(C)** for sequential, in-memory only.
- **Baseline throughput for ordinary files:** **(D)**.
- **Key interaction:** (A) shrinks the cache, so do **(A) first for safety**, then measure, then **(B)+(E)**
  to make the now-smaller-cache misses cheap, then **(D)**, then **(C)** if still short. Keep a 10h test run
  between changes per the batch workflow.

### Applicability across compression formats (analysed 2026-06-24)

Only **gz** and **bzip2** have true in-memory random-access seekable decoders. The other formats return
`null` from `GetSeekableStream()`, so large partitions fall back to a **disk** cache (see Batch 7). Small
partitions of *any* format use `SeekableStreamUsingRestarts` (in-memory, re-decompress-from-start) via the
perf-test "sequential" branch, so the disk fallback only bites **large** partitions of the no-native-seek
formats.

| Format | Seekable strategy (large partition) | In-memory? | Decompress-and-discard | Batch 6 opts that apply |
|--------|-------------------------------------|-----------|------------------------|-------------------------|
| gz     | gztool index (`GZipStreamSeekable`) | yes | between checkpoints | A, B, C, D, E |
| bzip2  | block index (`Bzip2StreamSeekable`) | yes | per block (≤900 KB) | A, D, E; C partial; **B n/a** (block size fixed by source) |
| xz     | none → **disk `cache.train`**       | **no (disk)** | n/a | A, D — **needs Batch 7** |
| zstd   | none → **disk `cache.train`**       | **no (disk)** | n/a | A, D — **needs Batch 7** |
| lz4    | none → **disk `cache.train`**       | **no (disk)** | n/a | A, D — **needs Batch 7** |
| lzip   | none → **disk `cache.train`**       | **no (disk)** | n/a | A, D — **needs Batch 7** |
| none   | raw `Multistream` (already seekable) | yes | none | A only (caching disk reads is marginal) |

**Batch 6 adjustments from this:**
- **(A) global cache budget and (D) item-stream reuse are format-agnostic** — they sit above/around the
  decompressor (the ¼-RAM `CachingStream` is added on *every* seekable path; the per-read `OpenItemStream`
  cost is identical regardless of compression). (A) remains the universal safety fix.
- **(B), (C), (E) are gz/bzip2-specific.** (B) denser-checkpoints is **gz-only** — bzip2's unit is the fixed
  ~900 KB block, so its analogue is just caching the decoded block (the `CachingStream` already does that),
  and its discard is bounded by block size (already less pathological than gz on scattered reads). (C)/(E)
  help gz and bzip2.

## Batch 7 — In-memory seekable zstd (ZstdSharp prefix-resume)  (flagged 2026-06-24, **DONE 2026-07-10** — awaiting suite)

**Implemented.** New `libZstd` project gives standard (non-seekable-format) zstd streams the same
in-memory random-access treatment as gz/bzip2, so large zstd partitions no longer extract to the
on-disk `cache.train`:

- **The zstd-specific hard part:** a zstd block can depend on decoder state beyond the 2 MB window —
  repeat offsets and entropy tables carried from earlier blocks — so unlike gzip, an arbitrary block
  boundary is NOT a safe resume point. Measured on real Clonezilla output: ~88% of boundaries resume
  correctly via [synthetic frame header + `ZSTD_DCtx_refPrefix(window)`]; the rest diverge, sometimes
  SILENTLY and **deep** (a long RLE/zero run neither uses nor updates repeat-offset state, so stale
  state can surface only megabytes later — observed on real data).
- **Correctness is therefore empirical, and the index bakes it in:** `ZstdSeekableIndex` builds by one
  sequential block-fed decode (ZstdSharp), arming a candidate point every ~64 MB and trial-decoding
  4 MB inline through a shadow decoder; accepted candidates are then **verified across their whole
  span** (parallel pass, piecewise MD5 vs the true decode at every candidate boundary). A point whose
  span diverges anywhere is **dropped and its predecessor re-verified over the merged span** — healing
  terminates because a frame-start resume IS the true decode (sound at any depth). If verification
  cannot converge, no index is produced and `DecompressorSelector` falls back to extraction as before.
- **Serving:** `ZstdStreamSeekable` (layer 3, like `GZipStreamSeekable`); reads are **clamped to their
  verified span** (state past a span's end is unproven — only output within it). Windows live
  zstd-compressed in the `.zsi` index file, loaded lazily (2 MB each; sparse windows shrink to ~KBs).
  `GetRecommendation` returns 32 MB-aligned sub-spans so the CachingStream above stays within
  Batch 6 (E)'s pooled-buffer sizes.
- **Wiring:** `ZstdDecompressor.GetSeekableStream()` via `IPartitionCache.GetZstdIndexFilename()`
  (`<partition>.zstd_index.zsi`). ZstdNet remains the sequential/train decoder; ZstdSharp (managed
  port) is used only where the advanced API is required.
- **Validated (scratchpad `zstproto`):** feasibility bench (88.5% boundary yield, divergences caught);
  end-to-end vs in-RAM ground truth on real images sdb1 (345 MB uncompressed: block starts, edges,
  cross-chunk reads, 200 random reads, reload-from-disk, sequential full-read MD5 — ALL PASS) and
  sda1 (417 MB, nearly incompressible: ALL PASS; index 12.5 MB); 19.9 GB sda2 at scale: **all 677
  points verified, none dropped** — 32.3 min build (8.8 pass-1 + 23.5 verify, E:-read-bound,
  one-time), 605.9 MB index (1.3% of the 45.4 GB uncompressed), cold random reads ~1.4 s each,
  ~1.1 GB peak build RAM.
- **Deferred as before:** xz / lz4 / lzip (see below); the 2 TB zst **drive image** will also index
  (data-region windows dominate index size; null-tail windows compress to ~nothing).
- **Build v2 (2026-07-10): single pass, verify-as-you-go, resumable.** The two-pass build (decode +
  4 MB trials, then a full re-read/re-decode verification pass with drop-and-merge healing) was
  replaced: two shadow decoders now run alongside the true decode — one insurance shadow covering
  the last confirmed point's open span, one for the current candidate — so every point's whole span
  is byte-verified against output already in hand, and the second 20 GB read disappears. A diverging
  candidate re-arms cheaply (~12% of boundaries, incl. long zero-stretches which the insurance
  shadow now covers end-to-end); a diverging CONFIRMED shadow (deeper than a whole span, never
  observed on real data) aborts indexing → extraction fallback. Sealed points are appended+flushed
  to the `.wip` incrementally (format v2 `ZSTZRAN2`, inline windows, gztool-style zeroed counts
  until finalisation), so: build RAM is ~10 MB flat (the v1 ~8 GB drive-image transient is gone by
  design) and an **interrupted build resumes** — fast-forwarding from the last sealed frame-start
  point to keep the truth chain rooted (a zstd point cannot checkpoint full decoder state the way a
  gzip point can, hence not gztool-cheap on single-frame sources). Measured on the 19.9 GB sda2:
  **21.4 min single-pass vs 32.3 two-pass**, identical 677 points / 605.9 MB index; sdb1+sda1
  byte-exact incl. crafted-interruption resume tests. Future squeeze if wanted: shadows currently
  share the main thread (build is CPU-bound at ~3× decode); offloading them is the next lever.
- **Follow-up (2026-07-10): drive images wired into the index paths too.** The bare drive-image flow
  (`CompressedImage`, e.g. `sda.img.gz`/`.img.zst`) passed no partition cache, so gz/zstd couldn't
  reach their index machinery there and ALWAYS train-extracted (gz drive listing was a 2 TB
  extraction all along). `DecompressorSelector` now synthesizes a `PartitionCache` rooted in the same
  whole-file hash folder the extraction cache uses (identical key → existing folders stay valid)
  whenever none was provided and the format is gz/zstd. Found+fixed in the same change: the hash
  helper left the compressed stream mid-position, which fed gztool a headless stream and produced a
  corrupt index (also revealed stdin-built gztool indexes are format v1 — parser handles both).
  Smoke-tested end-to-end on `sda1.img.gz`: index built (137 points), parsed in-process, listing
  correct, index reused on second run. Expect `ListContents.LargeDriveImages` Gz and Zst to change
  behaviour: index build replaces train extraction.
- **Superseded by the ZstdSeekable NuGet package (2026-07-11).** The in-repo `libZstd` implementation
  was upstreamed by the author into the standalone **ZstdSeekable 0.2.0** package
  (nuget.org, maintained at `C:\Users\Smith\Desktop\dev\cs\ZstdSeekable`), which carries the same v2
  engine: single-pass verified build (insurance + candidate shadows), flat ~10 MB build RAM,
  incremental `.wip` + resume, `ZSTZRAN2` format (loads `ZSTZRAN1` too), and — beyond our v2 — a
  divergence fallback that degrades to frame-start-only points instead of aborting. `libZstd` is
  deleted; `libClonezilla` now references the package. What stays in-repo is a thin bridge
  (`Decompressors/ZstdSeekableIntegration.cs`): `SeekableZstdStream` adapts `ZstdIndexedStream` to
  `IReadSuggestor` (32 MB-aligned sub-spans within a point's span — critical, or CachingStream's
  ~1 MB default segments cost 30-60× decode amplification on cold scans; Read fill-loops because
  CachingStream issues ONE `BaseStream.Read` per recommendation and requires it to cover the
  requested range, while `ZstdIndexedStream.Read` returns short at chunk boundaries — recommendations
  never cross one today, but the loop keeps that from being load-bearing), plus a Serilog
  `ILogger` bridge and a ~1 GB-interval build-progress reporter. `ZstdDecompressor` gates serving on
  index density (largest resume gap ≤ 4× `TargetSpanBytes`, else extraction fallback — a
  frame-start-only degraded index on a huge single-frame stream would serve worse than `cache.train`).
  Index filename/lookup unchanged (`<partition>.zstd_index.zsi`; ZSTZRAN2 is what v2 already wrote,
  so existing indexes stay valid). Validated via the rewritten `zstproto` harness driving the
  production path (`ZstdDecompressor` + `PartitionCache`) on sdb1: recommendations bounded+containing,
  200 random + span-boundary reads byte-exact vs in-RAM reference, sequential MD5 through a
  `CachingStream`+suggestor stack at 183 MB/s, reload-from-disk reuse — ALL PASS. Also hardened
  `GetRecommendation` at/past EOF to return an empty range (the old implementation threw).

> **Refer to this as "Batch 7".** Complements Batch 6; honours the same HARD CONSTRAINT (no disk
> materialisation of decompressed data). **Scope is zstd only** — xz / lz4 / lzip are deferred (see the end).

**Goal:** give zstd the same in-memory random-access treatment gz (gztool) and bzip2 (block index) already
have, so a large zstd partition no longer falls back to the on-disk `cache.train`. zstd is the highest-value
target (common modern Clonezilla default, `-z9` = `zstdmt`/`zstd`).

**Today:** `ZstdDecompressor.GetSeekableStream()` returns `null`, so for a large partition
`DecompressorSelector` extracts the whole decompressed stream to `cache.train` (recompressed zstd, on disk)
and serves random reads from that file — **violates the in-memory premise**. (Small partitions avoid it via
the in-memory `SeekableStreamUsingRestarts` branch.)

**What Clonezilla actually produces (measured from the `_zst` test image, sda2 18.9 GB):** a **standard
single zstd frame** (NOT the official seekable format — no seek-table skippable frame at EOF), window
**~2 MB** (`Window_Descriptor` 0x58, level ~3). So the cheap "frame index" does **not** apply — there's one
frame, and its blocks share the window. We need a **zran-style window-snapshot index.**

**Approach — zran for zstd via the prefix API:**
- zstd's advanced decode API resumes mid-stream from a **prefix**: `ZSTD_DCtx_refPrefix(window)` then decode
  forward. To resume at a zstd **block boundary** (blocks are ≤128 KB, so candidates are plentiful), hand the
  decoder the preceding **~2 MB window** (the exact decoded bytes) as the prefix, then decode on.
- **Index** = list of `{ uncompressedOffset, compressedOffset, 2 MB window snapshot }` at checkpoints spaced
  every span S (e.g. 64-256 MB). Built once by a single sequential decode (like the gz/bzip2 index build),
  cached to disk as a small index file (NOT the decompressed data). Windows loaded on demand at read time.
- **Index size** ≈ windowSize × (partitionSize ÷ S); e.g. 60 GB at S=256 MB ≈ ~480 MB, and each 2 MB window
  snapshot can be zstd-compressed in the index to roughly halve it. Tune S: larger S = smaller index but more
  decompress-per-seek. A read seeks to the nearest checkpoint ≤ target, sets the prefix, decodes ≤S forward.
- Mirrors the existing seekable design: it slots in at **layer 3** of the Batch 6 stack (a `ZstdStreamSeekable`
  returned by `GetSeekableStream()`), with the layer-4 `CachingStream` on top as usual.

**.NET path:** switch zstd from **`ZstdNet`** (native libzstd wrapper, no low-level resume) to
**`ZstdSharp`** (managed port of zstd 1.5.x, exposes the advanced/low-level API incl. `refPrefix` and frame
introspection). Reference impls to crib from: `derijkp/zstdra`, `zraorg/ZRA`.

**Once landed:** zstd inherits Batch 6's profile (decompress-and-discard within the span + the ¼-RAM cache),
so Batch 6 (A) global cache budget and (E) pooled buffers then apply to it too.

### Deferred (future batch) — xz / lz4 / lzip
Researched 2026-06-24; out of scope for Batch 7. Summary so the work isn't re-derived:
- **lz4** — 64 KB window → tiny gztool-class index (or a pure block index if the frame uses independent
  blocks). Easiest, but low value (rare). `.NET: K4os` + a little frame parsing.
- **xz** — *two regimes*: **pixz/`-z5p` (multi-block)** has a built-in block index → cheap, low effort
  (`org.tukaani.xz SeekableXZInputStream` model; `.NET: XZ.NET` already referenced). **plain `xz`/`-z5`
  (single block — what the `_xz` test image is: one 16.8 GB block, 4 MB dict)** → resume only at LZMA2
  chunk boundaries with 4 MB dict snapshots → needs a checkpoint-capable managed LZMA2 decoder. High effort.
- **lzip** — same shape: **plzip/`-z6p` (multi-member)** → cheap member index; **plain `lzip` (single
  member)** → LZMA dict snapshots, high effort.
