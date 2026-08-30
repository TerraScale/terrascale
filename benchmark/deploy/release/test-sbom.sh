#!/usr/bin/env bash
set -euo pipefail

script_dir=$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
tool=${TERRABASE_SBOM_TOOL:-$script_dir/sbom/target/release/terrabase-release-sbom}
[[ -f "$tool" && ! -L "$tool" && -x "$tool" ]] || {
  echo "SBOM generator is missing or unsafe: $tool" >&2
  exit 1
}
for dependency in jq sha256sum; do
  command -v "$dependency" >/dev/null || {
    echo "required SBOM test tool is missing: $dependency" >&2
    exit 1
  }
done

work=$(mktemp -d "${TMPDIR:-/tmp}/terrabase-sbom-test.XXXXXXXX")
cleanup() {
  if [[ -n ${work-} && -d "$work" && "$work" == */terrabase-sbom-test.* ]]; then
    rm -rf -- "$work"
  fi
}
trap cleanup EXIT

metadata=$work/metadata.json
artifact=$work/artifact.oci.tar
jq -n '
  {packages:[
    {id:"root 1.0.0 (path+file:///root)",name:"root",version:"1.0.0",license:"MIT"},
    {id:"dep 2.0.0 (registry+https://example.invalid)",name:"dep",version:"2.0.0",license:"Apache-2.0",source:"registry+https://example.invalid"}
  ],resolve:{nodes:[
    {id:"root 1.0.0 (path+file:///root)",deps:[{pkg:"dep 2.0.0 (registry+https://example.invalid)"}]},
    {id:"dep 2.0.0 (registry+https://example.invalid)",deps:[]}
  ]}}' >"$metadata"
printf 'artifact bytes\n' >"$artifact"

for output in "$work/first.cdx.json" "$work/second.cdx.json"; do
  "$tool" --metadata "$metadata" --root root --artifact "$artifact" \
    --target x86_64-unknown-linux-gnu --output "$output"
  jq -e '.bomFormat == "CycloneDX" and .specVersion == "1.6" and .version == 1' "$output" >/dev/null
done
cmp -- "$work/first.cdx.json" "$work/second.cdx.json"

assert_rejected() {
  local label=$1 input=$2
  if "$tool" --metadata "$input" --root root --artifact "$artifact" \
    --target x86_64-unknown-linux-gnu --output "$work/$label.out" >/dev/null 2>&1; then
    echo "$label unexpectedly succeeded" >&2
    return 1
  fi
}
missing_license=$work/missing-license.json
jq 'del(.packages[1].license)' "$metadata" >"$missing_license"
assert_rejected missing-license "$missing_license"
invalid_license=$work/invalid-license.json
jq '.packages[1].license = "DefinitelyNotSPDX"' "$metadata" >"$invalid_license"
assert_rejected invalid-license "$invalid_license"
symlink_metadata=$work/symlink-metadata.json
ln -s -- "$metadata" "$symlink_metadata"
assert_rejected symlink-metadata "$symlink_metadata"

echo "SBOM deterministic and hostile-input fixtures passed"
