#!/usr/bin/env bash
set -euo pipefail

native_dir=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
source "$native_dir/native-pin.env"
cache_dir="$native_dir/.cache"
runtimes_dir="$native_dir/runtimes"

sqlite_flags=(
  -O3 -fPIC -shared -g0
  -DSQLITE_THREADSAFE=1
  -DSQLITE_DQS=0
  -DSQLITE_ENABLE_FTS5
  -DSQLITE_ENABLE_JSON1
  -DSQLITE_ENABLE_PREUPDATE_HOOK
  -DSQLITE_ENABLE_SESSION
  -DSQLITE_ENABLE_MATH_FUNCTIONS
  -DSQLITE_DEFAULT_FOREIGN_KEYS=1
  -DSQLITE_TEMP_STORE=2
  -DSQLITE_OMIT_DEPRECATED
  -DSQLITE_USE_ALLOCA
  -DCODEC_TYPE=CODEC_TYPE_CHACHA20
)
sqlite_link_flags=(-Wl,-soname,libsqlite3mc.so -Wl,--build-id=sha1 -Wl,-z,noexecstack -lm -lpthread)
sqlite_symbols=(sqlite3_open_v2 sqlite3_key_v2 sqlite3_rekey_v2 sqlite3_preupdate_hook sqlite3_preupdate_old sqlite3_preupdate_new sqlite3session_create)
msquic_symbols=(MsQuicOpenVersion MsQuicClose)
numa_symbols=(numa_available numa_max_node)

fail() {
  echo "build-native: $*" >&2
  exit 1
}

require_tools() {
  local tool
  for tool in "$@"; do
    command -v "$tool" >/dev/null || fail "missing required tool: $tool"
  done
}

host_rid() {
  case "$(uname -m)" in
    x86_64) echo linux-x64 ;;
    aarch64 | arm64) echo linux-arm64 ;;
    *) fail "unsupported host architecture: $(uname -m)" ;;
  esac
}

rid_triplet() {
  case "$1" in
    linux-x64) echo x86_64-linux-gnu ;;
    linux-arm64) echo aarch64-linux-gnu ;;
    *) fail "unsupported runtime identifier: $1" ;;
  esac
}

rid_elf_machine() {
  case "$1" in
    linux-x64) echo "Advanced Micro Devices X86-64" ;;
    linux-arm64) echo "AArch64" ;;
    *) fail "unsupported runtime identifier: $1" ;;
  esac
}

fetch() {
  local url=$1 sha=$2 dest=$3
  if [[ -f "$dest" ]] && echo "$sha  $dest" | sha256sum --check --status; then
    echo "cached   $(basename "$dest")"
    return
  fi
  rm -f "$dest" "$dest.part"
  curl --fail --silent --show-error --location --output "$dest.part" "$url"
  if ! echo "$sha  $dest.part" | sha256sum --check --status; then
    rm -f "$dest.part"
    fail "SHA-256 mismatch for $url (expected $sha)"
  fi
  mv "$dest.part" "$dest"
  echo "fetched  $(basename "$dest") sha256=$sha"
}

verify_elf() {
  local rid=$1 library=$2
  local expected actual
  expected=$(rid_elf_machine "$rid")
  actual=$(readelf -h "$library" | awk -F': *' '/Machine:/ {print $2}')
  [[ "$actual" == "$expected" ]] || fail "$library targets '$actual', expected '$expected'"
}

verify_symbols() {
  local library=$1
  shift
  local exported symbol
  exported=$(nm -D --defined-only "$library" | awk '{sub(/@.*/, "", $NF); print $NF}')
  for symbol in "$@"; do
    grep -qx "$symbol" <<<"$exported" || fail "$library does not export $symbol"
    echo "verified $(basename "$library") exports $symbol"
  done
}

compiler_for() {
  local rid=$1
  local variable="CC_${rid//-/_}"
  if [[ -n "${!variable:-}" ]]; then
    echo "${!variable}"
  elif [[ "$rid" == "$(host_rid)" ]]; then
    echo "${CC:-cc}"
  else
    fail "cross-compiling libsqlite3mc.so for $rid needs $variable (for example $(rid_triplet "$rid")-gcc)"
  fi
}

build_sqlite() {
  local rid=$1 out=$2
  local work="$cache_dir/build/$rid/sqlite3mc"
  local compiler
  compiler=$(compiler_for "$rid")
  require_tools "${compiler%% *}"
  rm -rf "$work"
  mkdir -p "$work"
  tar -xzf "$sqlite_archive" -C "$work" --strip-components=1 autoconf/sqlite3.c autoconf/sqlite3.h autoconf/sqlite3ext.h
  grep -q "SQLite3 Multiple Ciphers $SQLITE3MC_VERSION\"" "$work/sqlite3.c" || fail "archive is not SQLite3 Multiple Ciphers $SQLITE3MC_VERSION"
  grep -q "#define SQLITE_VERSION *\"$SQLITE3MC_SQLITE_VERSION\"" "$work/sqlite3.c" || fail "archive is not SQLite $SQLITE3MC_SQLITE_VERSION"
  (
    cd "$work"
    export SOURCE_DATE_EPOCH=0
    $compiler "${sqlite_flags[@]}" -ffile-prefix-map="$work"=. sqlite3.c -o libsqlite3mc.so "${sqlite_link_flags[@]}"
  )
  install -m 0755 "$work/libsqlite3mc.so" "$out/libsqlite3mc.so"
  verify_elf "$rid" "$out/libsqlite3mc.so"
  verify_symbols "$out/libsqlite3mc.so" "${sqlite_symbols[@]}"
  grep -qa "SQLite3 Multiple Ciphers $SQLITE3MC_VERSION" "$out/libsqlite3mc.so" || fail "libsqlite3mc.so does not report SQLite3 Multiple Ciphers $SQLITE3MC_VERSION"
  echo "verified libsqlite3mc.so is SQLite3 Multiple Ciphers $SQLITE3MC_VERSION (SQLite $SQLITE3MC_SQLITE_VERSION)"
}

extract_msquic() {
  local rid=$1 out=$2
  local suffix="${rid//-/_}"
  suffix="${suffix^^}"
  local url_variable="MSQUIC_${suffix}_URL" sha_variable="MSQUIC_${suffix}_SHA256"
  local package="$cache_dir/libmsquic_${MSQUIC_VERSION}_$rid.deb"
  local triplet member
  triplet=$(rid_triplet "$rid")
  member="./usr/lib/$triplet/libmsquic.so.$MSQUIC_VERSION"
  fetch "${!url_variable}" "${!sha_variable}" "$package"
  ar p "$package" data.tar.xz | tar -xJO "$member" >"$cache_dir/libmsquic.so.2.$rid"
  install -m 0755 "$cache_dir/libmsquic.so.2.$rid" "$out/libmsquic.so.2"
  rm -f "$cache_dir/libmsquic.so.2.$rid"
  verify_elf "$rid" "$out/libmsquic.so.2"
  verify_symbols "$out/libmsquic.so.2" "${msquic_symbols[@]}"
  grep -qa "$MSQUIC_VERSION" "$out/libmsquic.so.2" || fail "libmsquic.so.2 does not carry version $MSQUIC_VERSION"
  echo "verified libmsquic.so.2 is MsQuic $MSQUIC_VERSION"
}

extract_numa() {
  local rid=$1 out=$2
  local suffix="${rid//-/_}"
  suffix="${suffix^^}"
  local url_variable="NUMA_${suffix}_URL" sha_variable="NUMA_${suffix}_SHA256"
  local package="$cache_dir/libnuma1_${NUMA_VERSION}_$rid.deb"
  local triplet member
  triplet=$(rid_triplet "$rid")
  member="./usr/lib/$triplet/libnuma.so.$NUMA_SO_VERSION"
  fetch "${!url_variable}" "${!sha_variable}" "$package"
  ar p "$package" data.tar.zst | zstd -dc | tar -xO "$member" >"$cache_dir/libnuma.so.1.$rid"
  install -m 0755 "$cache_dir/libnuma.so.1.$rid" "$out/libnuma.so.1"
  rm -f "$cache_dir/libnuma.so.1.$rid"
  verify_elf "$rid" "$out/libnuma.so.1"
  verify_symbols "$out/libnuma.so.1" "${numa_symbols[@]}"
  echo "verified libnuma.so.1 is numactl $NUMA_VERSION"
}

require_tools curl sha256sum tar ar zstd nm readelf install

rids=("$@")
if [[ ${#rids[@]} -eq 0 ]]; then
  rids=("$(host_rid)")
fi

mkdir -p "$cache_dir"
sqlite_archive="$cache_dir/sqlite3mc-$SQLITE3MC_VERSION-sqlite-$SQLITE3MC_SQLITE_VERSION-autoconf.tar.gz"
fetch "$SQLITE3MC_URL" "$SQLITE3MC_SHA256" "$sqlite_archive"

for rid in "${rids[@]}"; do
  rid_triplet "$rid" >/dev/null
  out="$runtimes_dir/$rid/native"
  mkdir -p "$out"
  echo "== $rid"
  build_sqlite "$rid" "$out"
  extract_msquic "$rid" "$out"
  extract_numa "$rid" "$out"
  (cd "$out" && sha256sum libsqlite3mc.so libmsquic.so.2 libnuma.so.1)
done
