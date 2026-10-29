#!/usr/bin/env bash
set -euo pipefail

resolved=$(mktemp)
trap 'rm -f "$resolved"' EXIT
# Constraints preserve reviewed versions; added/removed/incompatible inputs
# change the result and require an explicit lock update.
bash "$(dirname "$0")/compile-ci-lock.sh" \
  --constraints requirements-ci.txt --output-file "$resolved"
diff -u requirements-ci.txt "$resolved"
uv pip compile requirements-audit.in --python-version 3.12 \
  --python-platform x86_64-unknown-linux-gnu --no-build --generate-hashes --no-annotate \
  --custom-compile-command 'See docs/audit-toolchain.md' \
  --constraints requirements-ci.txt --constraints requirements-audit.txt --output-file "$resolved"
diff -u requirements-audit.txt "$resolved"
python "$(dirname "$0")/check-build-requirements.py"
