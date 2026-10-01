#!/usr/bin/env bash
set -euo pipefail

repo=$(cd "$(dirname "$0")/../.." && pwd)
printf 'localhost/terrascale/ts-auth:%s\n' "$(git -C "$repo" rev-parse HEAD)"
