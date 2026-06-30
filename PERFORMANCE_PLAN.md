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
- [skip] **P9. Drop `Synchronized` wrapper in `IndependentStream`** — `IndependentStream.cs:37`. Tempting
  (the `ReadLock` already serializes Read/Seek), **but** `Length`/`Position` getters are unlocked, so the
  inner `Synchronized` is what makes those atomic against in-flight seeks on the shared base stream. Removing
  it risks subtle races for a micro-gain. **Decision: leave as-is** unless a profile proves it matters, in
  which case lock `Length`/`Position` explicitly first.
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

> **Resolved by the full-suite run (06-29, D6-fix, 693 min, 74/74 green):** Gz (62.4→40.4) and Zst
> (48.8→33.9) **recovered to baseline**, but **Xz is still +105.7 min** (270.7→248.1 vs 142.4 baseline).
> With D6's churn gone, the only thing still inflating Xz is **D3's per-read reopen** re-establishing the
> expensive xz read position every read (zst/gz reopen cheaply via native seek; xz goes through the costly
> train-cache path). **→ The D3 fix IS needed, specifically for xz.** Mount.AsFiles.xz (reads a couple
> files, not a tree-walk) was flat at ~1.5 min throughout — confirming the cost is read-count × per-reopen,
> not decode per se.
- [ ] **Fix D3 — item-stream reuse with worker affinity (= Batch 6 (D)).** **Confirmed needed** by the
  full-suite run: Xz still +105.7 min after the D6 fix. Stop re-opening the native item per `Read`; cache the
  open item per handle and reuse across consecutive reads, yielding the worker under contention so opens stay
  unbounded (don't regress to "handle holds a worker"). (`libClonezilla/Extractors/PooledNativeItemStream.cs`
  + `NativeExtractorPool.cs`.) Validate on the **Xz** large-drive-image proxy (~4 h): expect 248 → ~142.

**Validation:** after both fixes, full suite — expect `ListContents.LargeDriveImages` (esp. Xz) back to ~baseline.

- **Restore everything to HEAD:** `git checkout HEAD -- libClonezilla/Extractors/ libDokan/VFS/DokanVFS.cs`

## Investigation I0 — gz seekable decode spawns gztool per read  (flagged 2026-06-25, DO BEFORE Batch 6)

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

- **(A) Global cache budget** — layer 4 + sizing in `DecompressorSelector.cs:280`. Share one ¼-RAM budget
  across all partitions (simplest: ¼ RAM ÷ partition-count, since all caches are created at mount; better: a
  cross-partition LRU that evicts a finished partition's blocks to feed the active one). **Fixes root #1**
  (the exhaustion / `0x800705AA`). Near-term safety fix. Tension: shrinks each cache → more misses → leans on (B)/(E).
- **(B) Denser gztool checkpoints** — layer 3 (index build in `GZipStreamSeekable`). More access points →
  less decompress-per-cold-seek → faster scattered reads **and** far less discard garbage. Highest-leverage
  for root #2. Tension: bigger index (disk + the index windows held in memory — still in-memory, just more).
- **(C) In-memory readahead** — layer 4 (or a thin layer between 3 and 4). On a miss, decompress a larger
  forward span and cache it. Helps **sequential** access; limited for scattered/fragmented. **No disk
  materialisation** — the rejected "decompress-once-to-scratch" variant is explicitly out (see HARD CONSTRAINT).
- **(D) Item-stream reuse** — layers 7-8. Stop re-opening the `IInArchiveGetStream` (`NativeItemStream`) on
  every `ReadFile`; cache it per handle and reuse for consecutive reads, with light worker affinity that
  yields under contention (keeps D3's unbounded opens). **Fixes the ~1-4 MB/s baseline for normal files**
  (per-read native + cluster-run-resolution overhead). Tension: a held item stream pulls back toward
  "handle holds a worker" (what D3 removed) — the affinity-with-yield hybrid is the way.
- **(E) Pooled decompress buffers** — layer 3 (and 4). Reuse the large `byte[]` decompression-output buffers
  (`ArrayPool`) instead of allocating per read. Targets the **LOH fragmentation / `gcFrag` spikes**.
  Complements (B): (B) cuts how much is discarded, (E) cuts the alloc cost of whatever is.
- **Free win (not a stream change):** skip `pagefile.sys` / `hiberfil.sys` / `swapfile.sys` (huge, useless).

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

## Batch 7 — In-memory seekable zstd (ZstdSharp prefix-resume)  (flagged 2026-06-24, NOT yet done)

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
