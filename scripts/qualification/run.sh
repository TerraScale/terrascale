#!/usr/bin/env bash
set -euo pipefail

phase=${1:?usage: run.sh seed\|verify\|exercise\|cleanup}
case "$phase" in seed|verify|exercise|cleanup) ;; *) echo "invalid phase: $phase" >&2; exit 2 ;; esac
for name in TERRASCALE_API_URL TERRASCALE_AUTH_URL TERRASCALE_PROJECT_ID TERRASCALE_DATABASE_ID TERRASCALE_API_KEY TERRASCALE_JOURNEY_RUN_ID TERRASCALE_SDK_CRATE TERRASCALE_SDK_CRATE_SHA256 TERRASCALE_SDK_DERIVE_CRATE TERRASCALE_SDK_DERIVE_CRATE_SHA256; do
  if [[ -z "${!name:-}" ]]; then
    printf '{"phase":"%s","outcome":"blocked","detail":"missing required environment variable %s"}\n' "$phase" "$name"
    exit 1
  fi
done

tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

verify_artifact() {
  local label=$1 path=$2 expected=$3 actual
  if [[ ! -f "$path" || -L "$path" ]]; then
    printf '{"phase":"%s","outcome":"blocked","detail":"%s is not a regular file"}\n' "$phase" "$label"
    exit 1
  fi
  actual=$(sha256sum -- "$path" | awk '{print $1}')
  if [[ "$actual" != "$expected" ]]; then
    printf '{"phase":"%s","outcome":"blocked","detail":"%s SHA-256 mismatch"}\n' "$phase" "$label"
    exit 1
  fi
}

# Verify both independently before extracting either archive or invoking Cargo.
verify_artifact TERRASCALE_SDK_CRATE "$TERRASCALE_SDK_CRATE" "$TERRASCALE_SDK_CRATE_SHA256"
verify_artifact TERRASCALE_SDK_DERIVE_CRATE "$TERRASCALE_SDK_DERIVE_CRATE" "$TERRASCALE_SDK_DERIVE_CRATE_SHA256"

archive_root() {
  local label=$1 archive=$2 expected_prefix=$3 listing=$4 root
  if ! tar -tzf "$archive" >"$listing" 2>/dev/null; then
    printf '{"phase":"%s","outcome":"blocked","detail":"%s is not a valid gzip archive"}\n' "$phase" "$label"
    exit 1
  fi
  root=$(awk -v prefix="$expected_prefix" 'index($0, prefix) == 1 { candidate=$1; sub(/\/.*$/, "", candidate); if (candidate != "") { print candidate; exit } }' "$listing")
  if [[ -z "$root" || "$root" != "$expected_prefix"* || "$root" == *".."* ]]; then
    printf '{"phase":"%s","outcome":"blocked","detail":"%s has an invalid package root"}\n' "$phase" "$label"
    exit 1
  fi
  if awk -F/ -v root="$root" '$1 != root && $1 != "" { bad=1 } END { exit bad }' "$listing"; then :; else
    printf '{"phase":"%s","outcome":"blocked","detail":"%s contains entries outside its package root"}\n' "$phase" "$label"
    exit 1
  fi
  printf '%s\n' "$root"
}

sdk_root=$(archive_root TERRASCALE_SDK_CRATE "$TERRASCALE_SDK_CRATE" terrascale- "$tmp/sdk.list")
derive_root=$(archive_root TERRASCALE_SDK_DERIVE_CRATE "$TERRASCALE_SDK_DERIVE_CRATE" terrascale-derive- "$tmp/derive.list")
if [[ "$sdk_root" != terrascale-* || "$sdk_root" == terrascale-derive-* || "$derive_root" != terrascale-derive-* ]]; then
  printf '{"phase":"%s","outcome":"blocked","detail":"archive package roots are not the intended SDK and derive packages"}\n' "$phase"
  exit 1
fi

mkdir "$tmp/sdk" "$tmp/derive"
tar -xzf "$TERRASCALE_SDK_CRATE" -C "$tmp/sdk"
tar -xzf "$TERRASCALE_SDK_DERIVE_CRATE" -C "$tmp/derive"
sdk_manifest="$tmp/sdk/$sdk_root/Cargo.toml"
derive_manifest="$tmp/derive/$derive_root/Cargo.toml"
if [[ ! -f "$sdk_manifest" || -L "$sdk_manifest" || ! -f "$derive_manifest" || -L "$derive_manifest" ]]; then
  printf '{"phase":"%s","outcome":"blocked","detail":"archive package manifest is missing or not regular"}\n' "$phase"
  exit 1
fi

manifest_value() { awk -F'"' -v key="$1" '$0 ~ "^" key "[[:space:]]*=" { print $2; exit }' "$2"; }
sdk_name=$(manifest_value name "$sdk_manifest"); sdk_version=$(manifest_value version "$sdk_manifest")
derive_name=$(manifest_value name "$derive_manifest"); derive_version=$(manifest_value version "$derive_manifest")
if [[ "$sdk_name" != terrascale || "$derive_name" != terrascale-derive || -z "$sdk_version" || -z "$derive_version" || "$sdk_root" != "terrascale-$sdk_version" || "$derive_root" != "terrascale-derive-$derive_version" ]]; then
  printf '{"phase":"%s","outcome":"blocked","detail":"package manifest name/version does not match archive root"}\n' "$phase"
  exit 1
fi
dependency_version=$(awk -F'"' '/^\[dependencies\.terrascale-derive\]/{seen=1; next} /^\[/{seen=0} seen && /^version[[:space:]]*=/{print $2; exit}' "$sdk_manifest")
if [[ "$dependency_version" != "$derive_version" ]]; then
  printf '{"phase":"%s","outcome":"blocked","detail":"SDK manifest has no exact terrascale-derive dependency"}\n' "$phase"
  exit 1
fi

# Bind the normalized SDK manifest to the exact verified derive tree.
python3 - "$sdk_manifest" "$derive_version" <<'PY'
from pathlib import Path
import sys
path = Path(sys.argv[1])
version = sys.argv[2]
text = path.read_text()
needle = '[dependencies.terrascale-derive]'
start = text.find(needle)
if start < 0:
    raise SystemExit(1)
end = text.find('\n[', start + len(needle))
if end < 0:
    end = len(text)
block = f'{needle}\npath = "../../derive/terrascale-derive-{version}"\nversion = "{version}"\n'
path.write_text(text[:start] + block + text[end:])
PY
cp "$(dirname "${BASH_SOURCE[0]}")/Cargo.toml" "$tmp/Cargo.toml"
cp "$(dirname "${BASH_SOURCE[0]}")/src/main.rs" "$tmp/main.rs"
sed -i "s#terrascale = { path = \"../../\" }#terrascale = { path = \"$tmp/sdk/$sdk_root\" }#" "$tmp/Cargo.toml"
mkdir "$tmp/src"; mv "$tmp/main.rs" "$tmp/src/main.rs"
echo "package=$sdk_root sha256=$(sha256sum -- "$TERRASCALE_SDK_CRATE" | awk '{print $1}') derive=$derive_root derive_sha256=$(sha256sum -- "$TERRASCALE_SDK_DERIVE_CRATE" | awk '{print $1}')"
nice -n 19 env CARGO_BUILD_JOBS=2 cargo run --manifest-path "$tmp/Cargo.toml" -- "$phase"
