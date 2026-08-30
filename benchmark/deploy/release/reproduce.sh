#!/usr/bin/env bash
set -euo pipefail

[[ $# -eq 3 ]] || {
  echo "usage: deploy/release/reproduce.sh <server|studio> <rust-target> <output-directory>" >&2
  exit 2
}
component=$1
target=$2
output=$3
script_dir=$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)

work=$(mktemp -d "${TMPDIR:-/tmp}/terrabase-reproduce.XXXXXXXX")
cleanup() {
  if [[ -n ${work-} && -d "$work" && "$work" == */terrabase-reproduce.* ]]; then
    rm -rf -- "$work"
  fi
}
trap cleanup EXIT
comparison=$work/comparison

cargo build \
  --manifest-path "$script_dir/sbom/Cargo.toml" \
  --locked \
  --release \
  --target-dir "$work/sbom-target"
export TERRABASE_SBOM_TOOL=$work/sbom-target/release/terrabase-release-sbom
[[ -f "$TERRABASE_SBOM_TOOL" && ! -L "$TERRABASE_SBOM_TOOL" && -x "$TERRABASE_SBOM_TOOL" ]]

"$script_dir/package.sh" "$component" "$target" "$output"
"$script_dir/package.sh" "$component" "$target" "$comparison"
diff --no-dereference --recursive --brief "$output" "$comparison"
"$script_dir/verify.sh" --unsigned "$output"

echo "reproducible unsigned package ready at $output"
