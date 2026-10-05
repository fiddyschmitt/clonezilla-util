#!/bin/bash
# Runs the test suite (everything in clonezilla-util_tests - the GoldenReferenceQuartet lives in
# its own project and is NOT part of this; run it deliberately with
#   dotnet test clonezilla-util_tests_golden -c Release
# ~40h). Run from Git Bash on the test box:
#   bash scripts/run-suite.sh            # all chunks, ~2h warm, results in test-results/
#
# Why chunked: the unfiltered assembly died four times on this box with a traceless
# "Test host process crashed", and a crash costs the whole run. Segments mean a crash costs one
# segment, names it, and lets the rest finish. Also encoded here, learned the hard way:
#  - default console verbosity prints NO per-test lines, which once made "0 tests completed" look
#    like slow progress when the run had aborted hours earlier -> detailed verbosity, and grep
#    "Test Run Aborted" explicitly.
#  - the exe under test is Main.ExeUnderTest (the published build) - republish before running if
#    the product changed, and NEVER republish mid-run (the bundler overwrites the exe under the
#    running mount).
#  - judge results from chunk-*.txt ("Total tests:" / "Passed:" / "Failed:" / "Test Run Aborted"),
#    and check their mtimes against this run's start: stale files from an earlier run otherwise
#    read as fresh results.
#  - launch DETACHED for long runs (nohup ... & disown) and poll the files; an interactive shell
#    that dies takes the driver with it while the children keep running.

set -u
REPO="$(cd "$(dirname "$0")/.." && pwd)"
KT="$REPO/scripts/killtool.sh"
OUT="$REPO/test-results"
PROJ="$REPO/clonezilla-util_tests/clonezilla-util_tests.csproj"
mkdir -p "$OUT"
O="$OUT/suite-summary.txt"
cd "$REPO"

# name|hang|filter  - ordered fast-first so problems surface early; bleed-stress last (~55 min,
# 4 codecs x kill-bursts + DOP-24 verify)
#
# hang = the chunk's --blame-hang-timeout: if any ONE test runs longer, the test host (and the exe under
# it) is killed and the chunk reports "Test Run Aborted", so a hung exe can no longer stall the detached
# run forever (gh review F52). Runaway protection only - several times the slowest cold test in the chunk
# (2026-09-23 cold run: mount-small's Partclone.dd 90 min, list-large's gz drive image 50 min), and above
# any [Timeout] in the chunk so a test's own timeout + [TestCleanup] gets the first chance to clean up
# (list-small 90m > the bzip2 self-heal test's 60; extract 150m > the per-codec and ext4 extracts' 120).
CHUNKS=(
  "unit|60m|FullyQualifiedName~PartcloneContentMapTests|FullyQualifiedName~SparseTests|FullyQualifiedName~clonezilla_util_tests.Memory.|FullyQualifiedName~clonezilla_util_tests.Ntfs."
  "list-small|90m|FullyQualifiedName~ListContents.SmallPartitionImages|FullyQualifiedName~ListContents.SmallClonezillaPartitions|FullyQualifiedName~ListContents.Partclone|FullyQualifiedName~ListContents.OutputFormat|FullyQualifiedName~clonezilla_util_tests.Cache.|FullyQualifiedName~clonezilla_util_tests.ErrorExits."
  "mount-small|300m|FullyQualifiedName~Mount.AsFiles.SmallPartitionImages|FullyQualifiedName~Mount.AsFiles.SmallClonezillaPartitions|FullyQualifiedName~Mount.AsFiles.Misc|FullyQualifiedName~Mount.AsFiles.Ext4|FullyQualifiedName~Mount.AsFiles.UbuntuFileSystems|FullyQualifiedName~Mount.AsFiles.Partclone|FullyQualifiedName~Mount.AsFiles.SearchPatterns"
  "mount-luks|60m|FullyQualifiedName~Mount.AsFiles.LuksClonezillaImages|FullyQualifiedName~Mount.AsFiles.LuksParcloneImages"
  "image-files|60m|FullyQualifiedName~Mount.AsImageFiles.ImageFileTests"
  "mount-large|60m|FullyQualifiedName~Mount.AsFiles.LargeClonezillaImages|FullyQualifiedName~Mount.AsFiles.LargeDriveImages"
  "list-large|300m|FullyQualifiedName~ListContents.LargeClonezillaPartitions|FullyQualifiedName~ListContents.LargeDriveImages|FullyQualifiedName~ListContents.OtherFormats"
  "extract|150m|FullyQualifiedName~clonezilla_util_tests.Extract.|FullyQualifiedName~clonezilla_util_tests.Synthetic."
  "bleed-stress|200m|FullyQualifiedName~Mount.AsFiles.ConcurrentBleedStress"
)

rm -f "$OUT"/chunk-*.txt "$OUT"/suite-done.txt
{
  echo "=== chunked suite ($(date)) ==="
  echo "HEAD: $(git log --oneline -1)"
  echo "GoldenReferenceQuartet is a separate project/gate and is not included"
} > "$O"

for entry in "${CHUNKS[@]}"; do
  name="${entry%%|*}"
  rest="${entry#*|}"
  hang="${rest%%|*}"
  filter="${rest#*|}"
  out="$OUT/chunk-$name.txt"

  # clean slate per chunk: stray mounts from a crashed chunk, and OUR stale test hosts only
  taskkill //F //IM clonezilla-util.exe > /dev/null 2>&1
  bash "$KT" testhost > /dev/null 2>&1
  sleep 3

  echo "" >> "$O"
  echo "######## $name  ($(date +%H:%M:%S)) ########" >> "$O"
  dotnet test "$PROJ" -c Release --nologo --filter "$filter" \
      --blame-hang-timeout "$hang" --blame-hang-dump-type none \
      --logger "console;verbosity=detailed" > "$out" 2>&1
  code=$?

  total=$(grep -oP 'Total tests: \K\d+' "$out" | tail -1)
  passed=$(grep -oP '^\s+Passed: \K\d+' "$out" | tail -1)
  failed=$(grep -oP '^\s+Failed: \K\d+' "$out" | tail -1)
  aborted=$(grep -c "Test Run Aborted" "$out")
  echo "  exit=$code total=${total:-?} passed=${passed:-0} failed=${failed:-0} aborted=$aborted" >> "$O"
  if [ "$aborted" -gt 0 ]; then
    echo "  !! TEST HOST CRASHED in this chunk - remaining chunks still run" >> "$O"
  fi
  grep -E "^\s+Failed " "$out" | head -10 >> "$O"
done

taskkill //F //IM clonezilla-util.exe > /dev/null 2>&1
bash "$KT" testhost > /dev/null 2>&1

{
  echo ""
  echo "=== SUMMARY ==="
  for entry in "${CHUNKS[@]}"; do
    name="${entry%%|*}"
    o="$OUT/chunk-$name.txt"
    t=$(grep -oP 'Total tests: \K\d+' "$o" 2>/dev/null | tail -1)
    p=$(grep -oP '^\s+Passed: \K\d+' "$o" 2>/dev/null | tail -1)
    f=$(grep -oP '^\s+Failed: \K\d+' "$o" 2>/dev/null | tail -1)
    a=$(grep -c "Test Run Aborted" "$o" 2>/dev/null)
    printf "  %-12s total=%-3s passed=%-3s failed=%-3s%s\n" "$name" "${t:-?}" "${p:-0}" "${f:-0}" "$([ "${a:-0}" -gt 0 ] && echo '   [ABORTED]')"
  done
  echo "SUITE-COMPLETE $(date)"
} >> "$O"

echo done > "$OUT/suite-done.txt"
cat "$O"
