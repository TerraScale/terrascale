#!/usr/bin/env bash
set -euo pipefail
repo=$(cd "$(dirname "$0")/../.." && pwd)
cd "$repo"
: "${DATABASE_URL:?DATABASE_URL is required}" "${MATRIX_SURFACE:?MATRIX_SURFACE is required}"
export MATRIX_REPEAT=3
tool=${1:-all}
case "$tool" in all|prisma|drizzle|efcore|sqlalchemy|pgcli|psql) ;; *) exit 2 ;; esac
engine_dir=$(mktemp -d)
trap 'rm -rf "$engine_dir"' EXIT
case "$tool" in all|prisma|drizzle)
curl -fsS https://binaries.prisma.sh/all_commits/d1b3f377ca835d73fd50954a5e22128b9ebdd6b8/debian-openssl-3.0.x/schema-engine.gz -o "$engine_dir/schema-engine.gz"
gzip -d "$engine_dir/schema-engine.gz"
printf '7116e00278a793ee13db6f0a732c1aaec10f795a37bc02e3a7df3b06b2f1311f  %s/schema-engine\n' "$engine_dir" | sha256sum -c - >&2
chmod +x "$engine_dir/schema-engine"
export PRISMA_SCHEMA_ENGINE_BINARY="$engine_dir/schema-engine"
npm ci --prefix benchmark/orm-matrix/js --ignore-scripts --no-audit --no-fund >&2
esac
case "$tool" in all|efcore)
dotnet restore benchmark/orm-matrix/ef/OrmMatrix.Ef.csproj --locked-mode >&2
dotnet build benchmark/orm-matrix/ef/OrmMatrix.Ef.csproj -c Release --no-restore --nologo -v quiet >&2
esac
status=0
case "$tool" in all) node benchmark/orm-matrix/js/probe.mjs || status=1 ;; prisma|drizzle) node benchmark/orm-matrix/js/probe.mjs "$tool" || status=1 ;; esac
for client in sqlalchemy pgcli; do
    if [ "$tool" = all ] || [ "$tool" = "$client" ]; then
        uv run --no-project --python 3.14.10 --with-requirements benchmark/orm-matrix/python/requirements.lock python benchmark/orm-matrix/python/probe.py "$client" || status=1
    fi
done
case "$tool" in all|psql) bash benchmark/orm-matrix/psql/run.sh || status=1 ;; esac
case "$tool" in all|efcore) dotnet run --project benchmark/orm-matrix/ef/OrmMatrix.Ef.csproj -c Release --no-build --no-restore || status=1 ;; esac
exit "$status"
