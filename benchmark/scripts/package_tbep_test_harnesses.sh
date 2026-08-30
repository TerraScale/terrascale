#!/usr/bin/env bash
set -euo pipefail

readonly expected_target="tbep-test-harnesses"
readonly expected_output="artifacts/dependencies/terrabase-tbep-harnesses"
readonly production_target_relative="target/tbep-test-harnesses/production"
readonly peer_target_relative="target/tbep-test-harnesses/flatbuffers"
readonly production_relative="$production_target_relative/"\
"x86_64-unknown-linux-musl/release/tbep-production-harness"
readonly peer_relative="$peer_target_relative/"\
"x86_64-unknown-linux-musl/release/tbep-fbs-quic-peer"
readonly preparation_relative="target/tbep-test-harnesses/prepared.v1"
readonly binding_relative="target/tbep-test-harnesses/package-binding.v1"
readonly maximum_binary_bytes=$((512 * 1024 * 1024))

usage() {
  printf '%s\n' "Usage: scripts/package_tbep_test_harnesses.sh <prepare|package|verify>" \
    "  --repository-root PATH --target tbep-test-harnesses --output $expected_output"
}

fail() {
  printf 'TBEP harness package verification failed: %s\n' "$1" >&2
  exit 1
}

require_command() {
  command -v "$1" >/dev/null 2>&1 || fail "required command is unavailable: $1"
}

digest_file() {
  local path="$1"
  local digest remainder
  read -r digest remainder < <(sha256sum -- "$path")
  printf 'sha256:%s\n' "$digest"
}

file_bytes() {
  local path="$1"
  local bytes
  bytes="$(wc -c <"$path")"
  printf '%s\n' "${bytes//[[:space:]]/}"
}

require_no_link_components() {
  local relative="$1"
  local current="$repository_root"
  local component
  IFS='/' read -r -a components <<<"$relative"
  for component in "${components[@]}"; do
    current="$current/$component"
    [[ ! -L "$current" ]] || fail "path contains a symbolic link: $relative"
  done
}

require_clean_source() {
  local status
  status="$(git -C "$repository_root" status --porcelain=v1 --untracked-files=all)"
  [[ -z "$status" ]] || fail "the source tree is dirty"
}

require_static_x86_64_elf() {
  local path="$1"
  local label="$2"
  local header elf_type entry program_headers dynamic_headers line
  local has_executable_load=0

  header="$(LC_ALL=C od -An -t x1 -N32 -- "$path")"
  header="${header//[[:space:]]/}"
  ((${#header} == 64)) || fail "$label has a truncated ELF header"
  [[ "${header:0:8}" == "7f454c46" ]] || fail "$label is not an ELF executable"
  [[ "${header:8:2}" == "02" ]] || fail "$label is not an ELF64 executable"
  [[ "${header:10:2}" == "01" ]] || fail "$label is not a little-endian ELF executable"
  [[ "${header:12:2}" == "01" ]] || fail "$label has an unsupported ELF version"
  elf_type="${header:32:4}"
  [[ "$elf_type" == "0200" || "$elf_type" == "0300" ]] ||
    fail "$label is not an executable or position-independent executable"
  [[ "${header:36:4}" == "3e00" ]] || fail "$label is not an x86-64 ELF executable"
  entry="${header:48:16}"
  [[ "$entry" != "0000000000000000" ]] || fail "$label has no executable entry point"

  program_headers="$(LC_ALL=C readelf -W -L -h -l -- "$path" 2>&1)" ||
    fail "$label has malformed ELF program headers"
  while IFS= read -r line; do
    [[ ! "$line" =~ ^[[:space:]]*INTERP[[:space:]] ]] ||
      fail "$label embeds a program interpreter"
    if [[ "$line" =~ ^[[:space:]]*LOAD[[:space:]] &&
          "$line" =~ [[:space:]]E([[:space:]]|$) ]]; then
      has_executable_load=1
    fi
    [[ ! "$line" =~ ^[[:space:]]*LOAD[[:space:]].*RWE ]] ||
      fail "$label has a writable executable load segment"
    [[ ! "$line" =~ ^[[:space:]]*GNU_STACK[[:space:]].*RWE ]] ||
      fail "$label requests an executable stack"
  done <<<"$program_headers"
  [[ "$program_headers" != *"readelf: Error:"* &&
     "$program_headers" != *"readelf: Warning:"* ]] ||
    fail "$label has malformed ELF program headers"
  ((has_executable_load == 1)) || fail "$label has no executable load segment"

  dynamic_headers="$(LC_ALL=C readelf -W -L -d -- "$path" 2>&1)" ||
    fail "$label has a malformed ELF dynamic section"
  [[ "$dynamic_headers" != *"readelf: Error:"* &&
     "$dynamic_headers" != *"readelf: Warning:"* ]] ||
    fail "$label has a malformed ELF dynamic section"
  [[ "$dynamic_headers" != *"(NEEDED)"* ]] ||
    fail "$label depends on a shared library"
  [[ "$dynamic_headers" != *"(RPATH)"* && "$dynamic_headers" != *"(RUNPATH)"* ]] ||
    fail "$label embeds a runtime library search path"

  if [[ "$elf_type" == "0300" ]]; then
    [[ "$dynamic_headers" == *"(FLAGS_1)"*"PIE"* ]] ||
      fail "$label is a shared object rather than a position-independent executable"
  fi
}

require_binary() {
  local relative="$1"
  local label="$2"
  local path="$repository_root/$relative"
  local bytes
  require_no_link_components "$relative"
  [[ -f "$path" && ! -L "$path" ]] || fail "$label is missing or is not a regular file"
  [[ "$(stat -c '%a' -- "$path")" == "755" ]] || fail "$label mode must be 0755"
  bytes="$(file_bytes "$path")"
  ((bytes > 0 && bytes <= maximum_binary_bytes)) ||
    fail "$label is empty or exceeds the 512 MiB bound"
  require_static_x86_64_elf "$path" "$label"
}

snapshot_source() {
  source_commit="$(git -C "$repository_root" rev-parse --verify HEAD)"
  source_tree="$(git -C "$repository_root" rev-parse --verify 'HEAD^{tree}')"
  configuration_digest="$(digest_file "$repository_root/.terrascale/deploy.json")"
}

require_source_snapshot() {
  local expected_commit="$1"
  local expected_tree="$2"
  local expected_configuration="$3"
  snapshot_source
  [[ "$source_commit" == "$expected_commit" ]] || fail "the source commit changed while packaging"
  [[ "$source_tree" == "$expected_tree" ]] || fail "the source tree changed while packaging"
  [[ "$configuration_digest" == "$expected_configuration" ]] ||
    fail "the Deploy configuration changed while packaging"
}

write_preparation() {
  local preparation="$repository_root/$preparation_relative"
  local temporary
  mkdir -p -- "${preparation%/*}"
  require_no_link_components "$preparation_relative"
  temporary="$(mktemp "${preparation}.XXXXXX")"
  {
    printf 'schema=terrabase/tbep-test-harness-preparation/v1\n'
    printf 'target=%s\n' "$expected_target"
    printf 'source_commit=%s\n' "$source_commit"
    printf 'source_tree=%s\n' "$source_tree"
    printf 'configuration_digest=%s\n' "$configuration_digest"
    printf 'production_target=%s\n' "$production_target_relative"
    printf 'peer_target=%s\n' "$peer_target_relative"
  } >"$temporary"
  chmod 0644 "$temporary"
  mv -- "$temporary" "$preparation"
}

verify_preparation() {
  local preparation="$repository_root/$preparation_relative"
  local line key value
  local -A fields=()
  require_no_link_components "$preparation_relative"
  [[ -f "$preparation" && ! -L "$preparation" ]] ||
    fail "the fresh-build preparation binding is absent or linked"
  while IFS= read -r line || [[ -n "$line" ]]; do
    [[ "$line" == *=* ]] || fail "the fresh-build preparation binding is malformed"
    key="${line%%=*}"
    value="${line#*=}"
    case "$key" in
      schema | target | source_commit | source_tree | configuration_digest | \
        production_target | peer_target) ;;
      *) fail "the fresh-build preparation binding has the wrong field set" ;;
    esac
    [[ -n "$key" && -n "$value" && ! -v "fields[$key]" ]] ||
      fail "the fresh-build preparation binding has an empty or duplicate field"
    fields["$key"]="$value"
  done <"$preparation"
  ((${#fields[@]} == 7)) || fail "the fresh-build preparation binding has the wrong field set"
  [[ "${fields[schema]:-}" == "terrabase/tbep-test-harness-preparation/v1" ]] ||
    fail "the fresh-build preparation binding has the wrong schema"
  [[ "${fields[target]:-}" == "$expected_target" ]] ||
    fail "the fresh-build preparation binding has the wrong target"
  [[ "${fields[source_commit]:-}" == "$source_commit" ]] ||
    fail "the current source commit does not match the fresh-build preparation"
  [[ "${fields[source_tree]:-}" == "$source_tree" ]] ||
    fail "the current source tree does not match the fresh-build preparation"
  [[ "${fields[configuration_digest]:-}" == "$configuration_digest" ]] ||
    fail "the current Deploy configuration does not match the fresh-build preparation"
  [[ "${fields[production_target]:-}" == "$production_target_relative" ]] ||
    fail "the fresh-build preparation has the wrong production target directory"
  [[ "${fields[peer_target]:-}" == "$peer_target_relative" ]] ||
    fail "the fresh-build preparation has the wrong peer target directory"
}

verify_output() {
  local output_root="$repository_root/$expected_output"
  local production="$repository_root/$production_relative"
  local peer="$repository_root/$peer_relative"
  local production_output="$output_root/tbep-production-harness"
  local peer_output="$output_root/tbep-fbs-quic-peer"
  local entry name
  local -a entries

  require_no_link_components "$expected_output"
  [[ -d "$output_root" && ! -L "$output_root" ]] ||
    fail "the declared output is missing or is not a regular directory"
  shopt -s nullglob dotglob
  entries=("$output_root"/*)
  shopt -u nullglob dotglob
  ((${#entries[@]} == 2)) || fail "the declared output must contain exactly two files"
  for entry in "${entries[@]}"; do
    name="${entry##*/}"
    case "$name" in
      tbep-production-harness | tbep-fbs-quic-peer) ;;
      *) fail "the declared output contains unexpected entry: $name" ;;
    esac
  done

  require_binary "$expected_output/tbep-production-harness" "packaged production harness"
  require_binary "$expected_output/tbep-fbs-quic-peer" "packaged FlatBuffers QUIC peer"
  [[ "$(digest_file "$production")" == "$(digest_file "$production_output")" ]] ||
    fail "the packaged production harness differs from the locked Release binary"
  [[ "$(digest_file "$peer")" == "$(digest_file "$peer_output")" ]] ||
    fail "the packaged FlatBuffers QUIC peer differs from the locked Release binary"

  production_digest="$(digest_file "$production_output")"
  peer_digest="$(digest_file "$peer_output")"
  production_bytes="$(file_bytes "$production_output")"
  peer_bytes="$(file_bytes "$peer_output")"
}

write_binding() {
  local binding="$repository_root/$binding_relative"
  local temporary
  mkdir -p -- "${binding%/*}"
  require_no_link_components "$binding_relative"
  temporary="$(mktemp "${binding}.XXXXXX")"
  {
    printf 'schema=terrabase/tbep-test-harness-package/v1\n'
    printf 'target=%s\n' "$expected_target"
    printf 'source_commit=%s\n' "$source_commit"
    printf 'source_tree=%s\n' "$source_tree"
    printf 'configuration_digest=%s\n' "$configuration_digest"
    printf 'output_root=%s\n' "$expected_output"
    printf 'production_digest=%s\n' "$production_digest"
    printf 'production_bytes=%s\n' "$production_bytes"
    printf 'peer_digest=%s\n' "$peer_digest"
    printf 'peer_bytes=%s\n' "$peer_bytes"
  } >"$temporary"
  chmod 0644 "$temporary"
  mv -f -- "$temporary" "$binding"
}

verify_binding() {
  local binding="$repository_root/$binding_relative"
  local line key value
  local -A fields=()
  require_no_link_components "$binding_relative"
  [[ -f "$binding" && ! -L "$binding" ]] || fail "the local package binding is absent or linked"
  while IFS= read -r line || [[ -n "$line" ]]; do
    [[ "$line" == *=* ]] || fail "the local package binding is malformed"
    key="${line%%=*}"
    value="${line#*=}"
    case "$key" in
      schema | target | source_commit | source_tree | configuration_digest | output_root | \
        production_digest | production_bytes | peer_digest | peer_bytes) ;;
      *) fail "the local package binding has the wrong field set" ;;
    esac
    [[ -n "$key" && -n "$value" && ! -v "fields[$key]" ]] ||
      fail "the local package binding has an empty or duplicate field"
    fields["$key"]="$value"
  done <"$binding"
  ((${#fields[@]} == 10)) || fail "the local package binding has the wrong field set"
  [[ "${fields[schema]:-}" == "terrabase/tbep-test-harness-package/v1" ]] ||
    fail "the local package binding has the wrong schema"
  [[ "${fields[target]:-}" == "$expected_target" ]] ||
    fail "the local package binding has the wrong target"
  [[ "${fields[source_commit]:-}" == "$source_commit" ]] ||
    fail "the current source commit does not match the local package binding"
  [[ "${fields[source_tree]:-}" == "$source_tree" ]] ||
    fail "the current source tree does not match the local package binding"
  [[ "${fields[configuration_digest]:-}" == "$configuration_digest" ]] ||
    fail "the current Deploy configuration does not match the local package binding"
  [[ "${fields[output_root]:-}" == "$expected_output" ]] ||
    fail "the local package binding has the wrong output root"
  [[ "${fields[production_digest]:-}" == "$production_digest" ]] ||
    fail "the production harness digest does not match the local package binding"
  [[ "${fields[production_bytes]:-}" == "$production_bytes" ]] ||
    fail "the production harness size does not match the local package binding"
  [[ "${fields[peer_digest]:-}" == "$peer_digest" ]] ||
    fail "the FlatBuffers QUIC peer digest does not match the local package binding"
  [[ "${fields[peer_bytes]:-}" == "$peer_bytes" ]] ||
    fail "the FlatBuffers QUIC peer size does not match the local package binding"
}

[[ $# -ge 1 ]] || {
  usage >&2
  exit 2
}
mode="$1"
shift
[[ "$mode" == "prepare" || "$mode" == "package" || "$mode" == "verify" ]] || {
  usage >&2
  exit 2
}

repository_argument=""
target=""
output=""
while (($#)); do
  case "$1" in
    --repository-root)
      (($# >= 2)) || fail "--repository-root requires a path"
      repository_argument="$2"
      shift 2
      ;;
    --target)
      (($# >= 2)) || fail "--target requires a name"
      target="$2"
      shift 2
      ;;
    --output)
      (($# >= 2)) || fail "--output requires a path"
      output="$2"
      shift 2
      ;;
    *)
      fail "unknown argument: $1"
      ;;
  esac
done

[[ -n "$repository_argument" && -d "$repository_argument" ]] ||
  fail "--repository-root must name an existing directory"
repository_root="$(cd -- "$repository_argument" && pwd -P)"
git_root="$(git -C "$repository_root" rev-parse --show-toplevel 2>/dev/null)" ||
  fail "--repository-root is not a Git repository"
git_root="$(cd -- "$git_root" && pwd -P)"
[[ "$repository_root" == "$git_root" ]] || fail "--repository-root must be the Git root"
[[ "$target" == "$expected_target" ]] || fail "--target must be $expected_target"
[[ "$output" == "$expected_output" ]] || fail "--output must be $expected_output"

for required_command in git sha256sum wc od install mktemp readelf stat; do
  require_command "$required_command"
done
[[ -f "$repository_root/.terrascale/deploy.json" &&
   ! -L "$repository_root/.terrascale/deploy.json" ]] ||
  fail "the Deploy configuration is missing or linked"
require_no_link_components ".terrascale/deploy.json"
require_clean_source
snapshot_source
initial_commit="$source_commit"
initial_tree="$source_tree"
initial_configuration="$configuration_digest"

if [[ "$mode" == "prepare" ]]; then
  for absent_relative in \
    "$production_target_relative" \
    "$peer_target_relative" \
    "$preparation_relative" \
    "$binding_relative" \
    "$expected_output"; do
    require_no_link_components "$absent_relative"
    [[ ! -e "$repository_root/$absent_relative" &&
       ! -L "$repository_root/$absent_relative" ]] ||
      fail "fresh candidate path must be absent before building: $absent_relative"
  done
  write_preparation
  require_source_snapshot "$initial_commit" "$initial_tree" "$initial_configuration"
  require_clean_source
  printf 'TBEP harness build prepared: target=%s tree=%s\n' \
    "$expected_target" "$source_tree"
  exit 0
fi

verify_preparation
require_binary "$production_relative" "locked Release production harness"
require_binary "$peer_relative" "locked Release FlatBuffers QUIC peer"

cleanup_armed=0
cleanup_failed_package() {
  if ((cleanup_armed)); then
    rm -rf -- "$repository_root/$expected_output"
    rm -f -- "$repository_root/$binding_relative"
  fi
}
trap cleanup_failed_package EXIT

if [[ "$mode" == "package" ]]; then
  require_no_link_components "$expected_output"
  [[ ! -e "$repository_root/$expected_output" && ! -L "$repository_root/$expected_output" ]] ||
    fail "the declared output must be absent before packaging"
  production_source_digest="$(digest_file "$repository_root/$production_relative")"
  peer_source_digest="$(digest_file "$repository_root/$peer_relative")"
  mkdir -p -- "$repository_root/artifacts/dependencies"
  require_no_link_components "$expected_output"
  mkdir -- "$repository_root/$expected_output"
  cleanup_armed=1
  install -m 0755 -- "$repository_root/$production_relative" \
    "$repository_root/$expected_output/tbep-production-harness"
  install -m 0755 -- "$repository_root/$peer_relative" \
    "$repository_root/$expected_output/tbep-fbs-quic-peer"
  [[ "$production_source_digest" == "$(digest_file "$repository_root/$production_relative")" ]] ||
    fail "the production harness changed while packaging"
  [[ "$peer_source_digest" == "$(digest_file "$repository_root/$peer_relative")" ]] ||
    fail "the FlatBuffers QUIC peer changed while packaging"
  verify_output
  require_source_snapshot "$initial_commit" "$initial_tree" "$initial_configuration"
  write_binding
else
  verify_output
fi

require_source_snapshot "$initial_commit" "$initial_tree" "$initial_configuration"
verify_binding
require_clean_source
cleanup_armed=0
printf 'TBEP harness package verified: target=%s tree=%s files=2 bytes=%s\n' \
  "$expected_target" "$source_tree" "$((production_bytes + peer_bytes))"
printf 'tbep-production-harness %s %s\n' "$production_digest" "$production_bytes"
printf 'tbep-fbs-quic-peer %s %s\n' "$peer_digest" "$peer_bytes"
