#!/usr/bin/env bash
# Run once through ts-heavy-any: bash benchmark/dotnet/R1CaptureBench/run-pairs.sh BASE_SHA CANDIDATE_SHA
set -euo pipefail
baseline=${1:?baseline SHA required}
candidate=${2:?candidate SHA required}
exec 9>/tmp/r1-capture-benchmark.lock
flock -x 9
fixture=$(mktemp -d "${TMPDIR:-/tmp}/r1-capture-pairs.XXXXXXXX")
trap 'rm -rf -- "$fixture"' EXIT
root=$(git rev-parse --show-toplevel)
printf 'host=%s baseline=%s candidate=%s\n' "$(hostname)" "$(git rev-parse "$baseline^{commit}")" "$(git rev-parse "$candidate^{commit}")" >&2
dotnet --version >&2
lscpu >&2
lscpu -p=CPU,CORE,SOCKET >&2
topology=$(lscpu -p=CPU,CORE,SOCKET | sed '/^#/d')
core0=$(printf '%s\n' "$topology" | awk -F, '$1 == 0 {print $2 ":" $3}')
core2=$(printf '%s\n' "$topology" | awk -F, '$1 == 2 {print $2 ":" $3}')
[ -n "$core0" ] && [ -n "$core2" ] && [ "$core0" != "$core2" ] || { echo 'CPUs 0,2 must be separate physical cores' >&2; exit 1; }
for variant in baseline candidate; do
  revision=$baseline
  [ "$variant" != candidate ] || revision=$candidate
  mkdir "$fixture/$variant"
  git archive "$revision" | tar -x -C "$fixture/$variant"
  mkdir -p "$fixture/$variant/benchmark/dotnet/R1CaptureBench"
  cp "$root/benchmark/dotnet/R1CaptureBench/"{Program.cs,R1CaptureBench.csproj} "$fixture/$variant/benchmark/dotnet/R1CaptureBench/"
  # The archived checkout has no sibling SDK; use the same verified checkout for both builds.
  sdk=${TsDotnetRoot:-"$root/../ts-dotnet"}
  nice -n 19 dotnet build "$fixture/$variant/benchmark/dotnet/R1CaptureBench" -c Release -p:TsDotnetRoot="$sdk" -p:RestoreLockedMode=true >&2
done
for repeat in 1 2 3 4 5; do
  order='baseline candidate'
  [ $((repeat % 2)) -ne 0 ] || order='candidate baseline'
  for variant in $order; do
    uptime >&2
    systemd-run --user --wait --pipe \
      -p CPUQuota=200% -p MemoryMax=512M -p MemorySwapMax=0 \
      taskset -c 0,2 nice -n 19 dotnet \
      "$fixture/$variant/benchmark/dotnet/R1CaptureBench/bin/Release/net11.0/R1CaptureBench.dll" "$variant-$repeat"
  done
done
