#!/usr/bin/env bash
set -euo pipefail

[[ $# -eq 3 ]] || {
  echo "usage: deploy/release/candidate.sh <server|studio> <rust-target> <output-directory>" >&2
  exit 2
}
component=$1
target=$2
output=$3
script_dir=$(CDPATH= cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)

"$script_dir/reproduce.sh" "$component" "$target" "$output"
"$script_dir/sign.sh" "$output"
"$script_dir/verify.sh" "$output"

echo "reproducible signed candidate ready at $output"
