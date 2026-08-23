#!/bin/bash
# Scoped process kills for the test harness. NEVER use `taskkill /IM` on a generic image name
# here: it is machine-wide and was killing an unrelated project's test hosts and the user's own
# Git-Bash tails (2026-08-15). These match on COMMAND LINE, so they can only hit OUR processes:
#   tail     - the detached-mount feeders, which are exactly `tail -f /dev/null`
#   testhost - vstest hosts running this repo's test dlls (clonezilla-util_tests*)
# clonezilla-util.exe needs no scoping - the image name IS the product - so callers taskkill it
# directly. That still kills a mount the user started by hand; the suite assumes the box is its own.
case "$1" in
  tail)
    powershell -NoProfile -Command "Get-CimInstance Win32_Process -Filter \"Name='tail.exe'\" | Where-Object { \$_.CommandLine -match '/dev/null' } | ForEach-Object { Stop-Process -Id \$_.ProcessId -Force -ErrorAction SilentlyContinue }" ;;
  testhost)
    powershell -NoProfile -Command "Get-CimInstance Win32_Process -Filter \"Name='testhost.exe'\" | Where-Object { \$_.CommandLine -match 'clonezilla-util_tests' } | ForEach-Object { Stop-Process -Id \$_.ProcessId -Force -ErrorAction SilentlyContinue }" ;;
  *)
    echo "usage: killtool.sh tail|testhost" >&2; exit 2 ;;
esac
exit 0
