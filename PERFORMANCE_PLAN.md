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

## Batch 2 — Hot-path logging & cheap allocations  (broad, lowest risk)

Only touches log output and per-call allocations; no data-path change. Safe to bundle.

- [ ] **P2. Stop eager `Log.Debug($"…")` rendering on hot paths.** Global level is `Debug` with an active
  Debug sink, so these are fully rendered *and* dispatched; and `$"…"` renders before the call regardless.
  - `DokanVFS.Trace` (`DokanVFS.cs:38-47`) — every Dokan op; allocates `params object?[]` + LINQ
    `Select`/`Join`/`Format`; `ReadFile` pre-builds `"out "+bytesRead` + `offset.ToString()` per read.
  - `CachingStream.ReadInternal` (`CachingStream.cs:63, 82`) — per-read `BytesToString`.
  - `SeekableDecompressingStream.Read` (`:54, 57`) — per-read interpolation + **two `DateTime.Now`** for timing.
  - `LazyList.EnsureExists` / `GetEnumerator` (`LazyList.cs:66, 109`) — per-item log over millions of blocks.
  - Fix: gate with a cached `static readonly bool` from `Log.IsEnabled(LogEventLevel.Debug)`, or delete the
    per-read/per-item ones; use message templates where kept; replace timing `DateTime.Now` with
    `Stopwatch.GetTimestamp()` behind the level check.
  - **Risk: low** (logging only; the `DateTime.Now`→`Stopwatch` swap is behaviour-neutral).
- [ ] **P3. `BytesToString` allocates its units array every call** — `libCommon/Extensions.cs:187`.
  `string[] UNITS = [...]` → `static readonly`. Called pervasively from logging. **Risk: none.**
- [ ] **P12. `Buffers.ARBITRARY_*_SIZE_BUFFER` are branching properties** — `libCommon/Buffers.cs:13-41`.
  Re-evaluate `Environment.Is64BitProcess` per access → `static readonly` fields. **Risk: none.**
- [ ] **P13. Dead code:** unused `distanceFromCurrentPosition` in `DokanVFS.ReadFile` normal-read path. **Risk: none.**

**Verification:** full suite (mostly to confirm the logging changes didn't alter control flow).

---

## Batch 3 — Archive listing & index-build path

All affect the `list` / tree-build / bzip2-index path, so one 10h run exercises them together.

- [ ] **P6. Culture-sensitive `StartsWith` in 7z parsing** — `lib7Zip/SevenZipUtility.cs:69, 90-101`.
  `line.StartsWith("Path =")` etc. default to `CurrentCulture` (much slower than ordinal) and run over
  **every** output line for archives with up to millions of entries. Add `StringComparison.Ordinal` to all
  `StartsWith`. Bonus: replace `line.Replace("Path = ","")` with a slice (`line["Path = ".Length..]`).
  **Risk: low** (ordinal vs culture is identical for these ASCII literals).
- [ ] **P5. Boyer-Moore rebuilds tables every call** — `libDecompression/Utilities/BoyerMoore.cs:18-19`.
  `MakeCharTable`(256) + `MakeOffsetTable` rebuilt on every `IndexOf`; `BZip2BlockFinder.FindInstances`
  calls it in a loop over the whole compressed stream. Precompute once (overload taking prebuilt tables, or a
  small `readonly` searcher built once in `FindInstances`). **Risk: low/medium** (refactor of the index path —
  verify generated bzip2 indexes are identical to before).
- [ ] **P11. `RegexOptions.Compiled` on single-use patterns** — `libDokan/FindFilesPatternToRegex.cs`
  (`Convert`). `Compiled` only pays off over many matches; for use-once enumeration patterns it's a net
  pessimization (heavy IL emit). Drop `Compiled` on the dynamic pattern; keep it on the static reused regexes.
  **Risk: none** (identical match results).

**Verification:** full suite. Confirm `list` output and bzip2 `*.bzip2_index.json` contents are unchanged.

---

## Batch 4 — Stream-stack & traversal micro-opts

- [ ] **P4. `CachingStream` lookup + RAM accounting** — `libCommon/Streams/CachingStream.cs:59, 161`.
  Lookup `cache.FirstOrDefault(entry => …)` allocates a `this`-capturing closure **per read** → rewrite as a
  `for` loop (LRU front short-circuits). RAM eviction recomputes `cache.Sum(c => c.Length)` inside the
  `while` loop (O(n)/iteration) → maintain a running `long currentCacheSizeBytes` updated on insert/evict/clear.
  Optional: `LinkedList<CacheEntry>` for O(1) move-to-front. **Risk: medium** (running total must be updated at
  every mutation site: both cache-type paths, move-to-front Remove+Insert, and `Close()`).
- [ ] **P7. `Recurse` (IEnumerable overload) is O(n²)** — `libCommon/Extensions.cs:34`. `queue.RemoveAt(0)` on a
  `List` shifts every element. Swap to `Queue<T>` (O(1) dequeue; FIFO order is identical to the current
  List-as-queue). **Risk: low.**
- [ ] **P8. `Ancestors`/`FullPath`/`IsAccessibleToProcess`** — `libDokan/VFS/FileSystemEntry.cs:47-93`.
  `Ancestors` rebuilds (`Recurse().Reverse().ToList()`) each access; `FullPath` calls it twice;
  `IsAccessibleToProcess` (per `FindFiles`) always allocates the list **and** `new ProcInfo` although
  `RestrictedFolderByPID` is unused in practice. Short-circuit `IsAccessibleToProcess` (skip `ProcInfo` when no
  restricted ancestors); build the path once with a `Stack`/`StringBuilder`; optionally cache `FullPath`
  (nodes are immutable post-build). **Risk: low/medium** (only cache `FullPath` if nothing mutates the tree
  after build — verify).
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

## Notes / decisions

- Batches are independent; reorder freely. Value-first order is B1 > B2 > B3 > B4.
- B2/B3 are mostly "free" (byte-identical output) — if you want a low-anxiety first run to validate the
  batch+test workflow, start there instead of B1.
- P9 and P10 are intentionally **not** being done (concurrency risk / inherent cost) — see above.
- Correctness-review history is in `CODE_REVIEW_PLAN.md`; this file is performance-only.
