set -eu
matrix_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
exec nix shell github:NixOS/nixpkgs/31ecbaca1bdefa45dffc409f0250bd8039ea0010#postgresql_18 --command uv run --no-project --python 3.14.10 --with-requirements "$matrix_dir/python/requirements.lock" python "$matrix_dir/psql/probe.py"
