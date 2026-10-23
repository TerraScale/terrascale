#!/usr/bin/env bash
# TerraBase against the raw vendored Turso driver (VsTursoBenchmark.cs). Run from the repository root:
#   benchmark/dotnet/VsTurso/run.sh <out-file> [extra env: TERRABASE_VS_TURSO_SECONDS/REPEATS/LANES/WORKLOADS]
# The E2E test project runs with server GC, no tiered PGO and DI's reflection engine to suit a parallel suite; this run
# uses the production server's runtime settings instead (src/TerraBase/TerraBase.csproj), on a copy of its runtimeconfig.
set -euo pipefail
root=$(cd "$(dirname "$0")/../../.." && pwd)
out=$(realpath -m "${1:?usage: run.sh <out-file>}")
dotnet build "$root/tests/TerraBase.E2E" --configuration Release
bin="$root/tests/TerraBase.E2E/bin/Release/net11.0"
config="$bin/TerraBase.E2E.runtimeconfig.json"
jq '.runtimeOptions.configProperties |= (del(."Microsoft.Extensions.DependencyInjection.DisableDynamicEngine")
  + {"System.GC.Server": false, "System.GC.Concurrent": true, "System.Runtime.TieredPGO": true})' "$config" >"$config.vs-turso"
mv "$config.vs-turso" "$config"
TERRABASE_VS_TURSO_OUT="$out" dotnet exec "$bin/TerraBase.E2E.dll" --treenode-filter '/*/*/VsTursoBenchmark/*'
cat "$out"
