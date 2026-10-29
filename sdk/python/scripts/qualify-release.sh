#!/usr/bin/env bash
set -euo pipefail

[ "$#" -eq 1 ] || { echo "usage: qualify-release.sh <external-output-directory>" >&2; exit 2; }
cd "$(dirname "${BASH_SOURCE[0]}")/.."
repository=$(git rev-parse --show-toplevel)
output=$(realpath -m "$1")
case "$output/" in "$repository/"*) echo 'Release output must be outside the checkout' >&2; exit 2 ;; esac
mkdir -p "$output"
[ -z "$(find "$output" -mindepth 1 -maxdepth 1 -print -quit)" ] || { echo 'Release output must be empty' >&2; exit 2; }

workspace=$(mktemp -d)
trap 'rm -rf "$workspace"' EXIT
uv venv --python 3.12 "$workspace/venv"
export VIRTUAL_ENV="$workspace/venv" PATH="$workspace/venv/bin:$PATH"
uv pip sync --python "$VIRTUAL_ENV/bin/python" --require-hashes --only-binary :all: requirements-ci.txt
python scripts/check-build-requirements.py
bash scripts/check-ci-lock.sh
uv venv --python 3.12 "$workspace/audit"
uv pip sync --python "$workspace/audit/bin/python" --require-hashes --only-binary :all: requirements-audit.txt
"$workspace/audit/bin/pip-audit" --strict --disable-pip --require-hashes -r requirements-ci.txt -r requirements-audit.txt
uv pip install --python "$VIRTUAL_ENV/bin/python" --no-deps --no-build-isolation -e '.[dev]'
uv pip check --python "$VIRTUAL_ENV/bin/python"
ruff check src tests
mypy
pytest -q
uv build --python "$VIRTUAL_ENV/bin/python" --no-build-isolation --out-dir "$output"
twine check "$output"/*
