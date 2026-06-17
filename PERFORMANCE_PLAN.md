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

## Notes / decisions

- Batches are independent; reorder freely. Value-first order is B1 > B2 > B3 > B4.
- B2/B3 are mostly "free" (byte-identical output) — if you want a low-anxiety first run to validate the
  batch+test workflow, start there instead of B1.
- P9 and P10 are intentionally **not** being done (concurrency risk / inherent cost) — see above.
- Correctness-review history is in `CODE_REVIEW_PLAN.md`; this file is performance-only.
