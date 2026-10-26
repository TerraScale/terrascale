#!/usr/bin/env bash
set -euo pipefail

backend_requirements=$(mktemp)
trap 'rm -f "$backend_requirements"' EXIT
# uv reads project dependencies, but does not include build-system.requires.
python - <<'PY' > "$backend_requirements"
try:
    import tomllib
except ModuleNotFoundError:  # Python 3.10; tomli is part of the locked graph.
    import tomli as tomllib

with open("pyproject.toml", "rb") as project:
    for requirement in tomllib.load(project)["build-system"]["requires"]:
        print(requirement)
PY

uv pip compile pyproject.toml requirements-ci.in "$backend_requirements" --extra dev \
  --python-version 3.10 --python-platform x86_64-unknown-linux-gnu \
  --no-build --generate-hashes --no-annotate \
  --custom-compile-command 'See docs/dependencies.md' "$@"
