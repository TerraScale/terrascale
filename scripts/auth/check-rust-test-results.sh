#!/usr/bin/env bash
set -euo pipefail

if test "$#" -ne 1 || ! test -f "$1"; then
  echo "usage: $0 <cargo-test-log>" >&2
  exit 2
fi

awk '
  /^test result: / {
    results++
    if ($0 !~ /^test result: ok\./) {
      invalid++
      next
    }
    if (match($0, /[0-9]+ passed;/)) {
      count = substr($0, RSTART, RLENGTH)
      sub(/ passed;/, "", count)
      total_passed += count
    } else {
      malformed++
    }
  }
  END {
    if (results == 0 || invalid > 0 || malformed > 0 || total_passed < 1) {
      print "Rust test result guard failed: expected successful harness summaries with at least one passed test." > "/dev/stderr"
      exit 1
    }
    printf "Rust test result guard passed: %d tests passed across %d harnesses.\n", total_passed, results
  }
' "$1"
