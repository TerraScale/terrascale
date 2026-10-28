#!/usr/bin/env bash
# Attributes the traces of one api-common-profile-20261028.sh run; builds the analyzer under the repository's analyzers.
# Run through: TS_HEAVY_HOST=beta ts-heavy-any nice -n 19 benchmark/scripts/api-common-profile-analyze.sh <label> <attempt> --repo terrascale --branch <branch>
cd -- "$(dirname -- "$0")/../.."
set -euo pipefail
label=${1:?usage: api-common-profile-analyze.sh <run label> <attempt>}
attempt=${2:?usage: api-common-profile-analyze.sh <run label> <attempt>}
[[ "$label$attempt" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]] || { echo 'Invalid label.' >&2; exit 2; }
out=$PWD/benchmark/bench/common-profile-$label
analysis=$out/analysis-$attempt
mkdir "$analysis"
exec > "$analysis/job.log" 2>&1 < /dev/null
git rev-parse HEAD > "$analysis/harness-sha.txt"
(cd "$out" && sha256sum --check --quiet traces.sha256)
lib=$HOME/.nuget/packages/microsoft.diagnostics.tracing.traceevent/3.2.5/lib/netstandard2.0
cp benchmark/scripts/api-trace-attribution.cs "$analysis/Program.cs"
cat > "$analysis/TraceAttribution.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="Microsoft.Diagnostics.Tracing.TraceEvent" HintPath="$lib/Microsoft.Diagnostics.Tracing.TraceEvent.dll" />
    <Reference Include="Microsoft.Diagnostics.FastSerialization" HintPath="$lib/Microsoft.Diagnostics.FastSerialization.dll" />
  </ItemGroup>
</Project>
EOF
sha256sum "$lib/Microsoft.Diagnostics.Tracing.TraceEvent.dll" "$lib/Microsoft.Diagnostics.FastSerialization.dll" > "$analysis/traceevent.sha256"
dotnet build "$analysis/TraceAttribution.csproj" --configuration Release --disable-build-servers > "$analysis/build.log" 2>&1 \
  || { grep -E 'error|warn' "$analysis/build.log" | sort -u | head -40; exit 1; }
kinds='cpu alloc'
if [[ -e "$out/alloc-sampling" ]]; then kinds+=' sampled'; fi
for run in a b; do
  for cell in small-c1 small-c16; do
    for kind in $kinds; do
      trace=$out/$run/$cell-${kind/sampled/alloc}.nettrace
      dotnet exec "$analysis/bin/Release/net11.0/TraceAttribution.dll" "$trace" "$kind" "$(cat "$out/$run/owned.pid")" \
        "$out/$run/$cell-threads.tsv" > "$analysis/$run-$cell-$kind.md" 2> "$analysis/$run-$cell-$kind.err" \
        || echo "$run $cell $kind: $(cat "$analysis/$run-$cell-$kind.err")" >> "$analysis/rejected.txt"
    done
  done
done
(cd "$analysis" && sha256sum Program.cs TraceAttribution.csproj ./*.md > artifacts.sha256)
if [[ -e "$analysis/rejected.txt" ]]; then cat "$analysis/rejected.txt"; exit 1; fi
echo 'Analysis completed.'
