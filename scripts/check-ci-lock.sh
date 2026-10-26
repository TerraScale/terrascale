#!/usr/bin/env bash
set -euo pipefail

resolved=$(mktemp)
trap 'rm -f "$resolved"' EXIT
# Constraints preserve reviewed versions; added/removed/incompatible inputs
# change the result and require an explicit lock update.
bash "$(dirname "$0")/compile-ci-lock.sh" \
  --constraints requirements-ci.txt --output-file "$resolved"
diff -u requirements-ci.txt "$resolved"
python "$(dirname "$0")/check-build-requirements.py"
