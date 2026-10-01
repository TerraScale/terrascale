#!/usr/bin/env bash
set -euo pipefail

test "$#" -eq 0 || { echo "usage: $0" >&2; exit 2; }

repo=$(cd "$(dirname "$0")/../.." && pwd)
printf 'localhost/terrascale/ts-auth:%s\n' "$(git -C "$repo" rev-parse HEAD)"
