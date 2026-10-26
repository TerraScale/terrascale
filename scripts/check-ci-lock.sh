#!/usr/bin/env bash
set -euo pipefail

resolved=$(mktemp)
trap 'rm -f "$resolved"' EXIT
# Constraints preserve reviewed versions; added/removed/incompatible inputs
# change the result and require an explicit lock update.
uv pip compile pyproject.toml requirements-ci.in --extra dev \
  --python-version 3.10 --python-platform x86_64-unknown-linux-gnu \
  --no-build --generate-hashes --no-annotate \
  --custom-compile-command 'See docs/dependencies.md' \
  --constraints requirements-ci.txt --output-file "$resolved"
diff -u requirements-ci.txt "$resolved"
