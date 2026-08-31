#!/usr/bin/env bash
set -euo pipefail

[[ $# -eq 1 && -d $1 && ! -L $1 ]] || {
  echo "usage: $0 <cargo-target-directory>" >&2
  exit 2
}
root=$1
: "${CC:?CC must name the pinned compiler}"
: "${CXX:?CXX must name the pinned compiler}"
: "${CMAKE:?CMAKE must name the pinned CMake}"
: "${CMAKE_MAKE_PROGRAM:?CMAKE_MAKE_PROGRAM must name pinned Ninja}"
: "${PERL:?PERL must name pinned Perl}"
: "${GO:?GO must name pinned Go}"

cache_list=$(mktemp "${TMPDIR:-/tmp}/terrabase-fips-caches.XXXXXXXX")
cleanup() { rm -f -- "$cache_list"; }
trap cleanup EXIT
find "$root" -type f -path '*/aws-lc-fips-sys-*/out/build/CMakeCache.txt' -print0 \
  | LC_ALL=C sort -z >"$cache_list"
mapfile -d '' caches <"$cache_list"
((${#caches[@]} > 0)) || {
  echo "no AWS-LC FIPS CMake cache exists under $root" >&2
  exit 1
}

for cache in "${caches[@]}"; do
  [[ -f "$cache" && ! -L "$cache" ]]
  expect_cache_value() {
    local key=$1
    local expected=$2
    awk -v key="$key" -v expected="$expected" '
      index($0, key ":") == 1 {
        count++
        value = $0
        sub(/^[^=]*=/, "", value)
        if (value == expected) matches++
      }
      END { exit !(count == 1 && matches == 1) }
    ' "$cache"
  }
  expect_cache_value CMAKE_COMMAND "$CMAKE"
  expect_cache_value CMAKE_C_COMPILER "$CC"
  expect_cache_value CMAKE_CXX_COMPILER "$CXX"
  expect_cache_value CMAKE_MAKE_PROGRAM "$CMAKE_MAKE_PROGRAM"
  expect_cache_value PERL_EXECUTABLE "$PERL"
  expect_cache_value GO_EXECUTABLE "$GO"
  expect_cache_value CMAKE_GENERATOR Ninja
  expect_cache_value CMAKE_PROJECT_NAME AWS_LC_RUST
  grep -Eq '^AWSLC_SOURCE_DIR:[^=]+=.*/aws-lc-fips-sys-[^/]+/aws-lc$' "$cache"
  grep -Eq '^CMAKE_HOME_DIRECTORY:[^=]+=.*/aws-lc-fips-sys-[^/]+$' "$cache"
done

printf 'verified %d AWS-LC FIPS CMake cache(s)\n' "${#caches[@]}"
