#!/usr/bin/env bash
# Cached system-store open (TursoStorageEngine + StampingStorageEngine): one predeclared paired measurement of the keyed api
# read lane. Before is main a42f65d55's two engine files, after is this branch's; nothing else in the server differs.
# Builds both Release E2E arms against the pinned ts-dotnet, then six alternating fresh-process pairs (B A, A B, B A, A B, B A,
# A B) under a quiet reservation. No tests, no profiling, no retries; a failing arm stops the run and everything is kept.
# The assessment is predeclared in api-cached-system-open-summary.py.
# Run through: TS_HEAVY_HOST=beta ts-heavy-any nice -n 19 benchmark/scripts/api-cached-system-open-20261028.sh <label> --repo terrascale --branch <branch>
cd -- "$(dirname -- "$0")/../.."
set -euo pipefail
label=${1:?usage: api-cached-system-open-20261028.sh <fresh run label>}
[[ "$label" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]] || { echo 'Invalid run label.' >&2; exit 2; }
sdk_pin=36d114877a69d18fba33b27ad362bd3e68e9e7ff
baseline=a42f65d558384ed2728f2799c70fe98addf6924f
candidate=8aaef93e8e65d1b17e7d754f6911bc8e4630bf80
changed=(src/TerraBase/Engine/StampingStorageEngine.cs src/TerraBase/Engine/Turso/TursoStorageEngine.cs)
cpus=2-11
pairs=6
out=$PWD/benchmark/bench/cached-system-open-$label
mkdir "$out"
exec > "$out/job.log" 2>&1 < /dev/null
# No build daemon may outlive a build into the timed arms.
export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false DOTNET_CLI_USE_MSBUILD_SERVER=0
git rev-parse HEAD > "$out/harness-sha.txt"
echo "$baseline" > "$out/baseline-sha.txt"
echo "$candidate" > "$out/candidate-sha.txt"
git merge-base --is-ancestor "$baseline" HEAD
git merge-base --is-ancestor "$candidate" HEAD
# The only build inputs that differ from main are the two engine files; the harness adds benchmark/scripts only.
test "$(git diff --name-only "$candidate" HEAD | grep -v '^benchmark/scripts/' || true)" = ''
test "$(git diff --name-only "$baseline" HEAD -- src Directory.Build.props Directory.Build.targets Directory.Packages.props global.json \
  NuGet.config benchmark/dotnet tests/TerraBase.E2E tests/TerraBase.TestHarness)" = "$(printf '%s\n' "${changed[@]}")"
git diff "$baseline" HEAD -- "${changed[@]}" > "$out/production.diff"
git diff --quiet && git diff --cached --quiet
trap 'git checkout -q HEAD -- "${changed[@]}"' EXIT
sdk=$HOME/remote-builds/ts-dotnet/cached-system-open-$label
mkdir -p "$(dirname "$sdk")"
test ! -e "$sdk"
git clone --quiet --no-checkout https://github.com/TerraScale/ts-dotnet.git "$sdk"
git -C "$sdk" checkout --quiet --detach "$sdk_pin"
git -C "$sdk" rev-parse HEAD > "$out/sdk-sha.txt"
test "$(cat "$out/sdk-sha.txt")" = "$sdk_pin"
export TsDotnetRoot=$sdk
# Pinned Turso native assets: the script checks SHA256SUMS before and after the download.
scripts/vendor/download-turso-native.sh > "$out/turso-native.log" 2>&1
sha256sum src/vendor/Turso.Native/SHA256SUMS src/vendor/Turso.Data.Sqlite/native/runtimes/*/native/libturso_sdk_kit.so > "$out/turso-native.sha256"
make restore > "$out/restore.log" 2>&1 || { tail -40 "$out/restore.log"; exit 1; }

build() {
  sha256sum "${changed[@]}" > "$out/$1-source.sha256"
  dotnet build tests/TerraBase.E2E --configuration Release --no-restore > "$out/build-$1.log" 2>&1 || { tail -60 "$out/build-$1.log"; return 1; }
  grep -E '^ *[0-9]+ Warning\(s\)|^ *[0-9]+ Error\(s\)' "$out/build-$1.log" > "$out/build-$1.counts" || true
  mkdir "$out/$1"
  cp -a tests/TerraBase.E2E/bin/Release/net11.0/. "$out/$1/"
  rm -rf "$out/$1/TestResults"
  config=$out/$1/TerraBase.E2E.runtimeconfig.json
  # The established benchmark runtime: workstation concurrent GC with TieredPGO, the same in both arms.
  jq '.runtimeOptions.configProperties |= (del(."Microsoft.Extensions.DependencyInjection.DisableDynamicEngine")
    + {"System.GC.Server":false,"System.GC.Concurrent":true,"System.Runtime.TieredPGO":true})' "$config" > "$config.tmp"
  mv "$config.tmp" "$config"
  (cd "$out/$1" && sha256sum terrabase.dll TerraScale.DotNet.dll TerraBase.E2E.dll TerraBase.E2E.runtimeconfig.json) > "$out/$1.sha256"
  # TUnit rewrites TestResults/ in the arm folder on every run, so the all-files manifest leaves it out.
  (cd "$out/$1" && find . -type f -not -path './TestResults/*' -print0 | sort -z | xargs -0 sha256sum) > "$out/$1.all.sha256"
}
git checkout -q "$baseline" -- "${changed[@]}"
build before
git checkout -q HEAD -- "${changed[@]}"
build after
git diff --quiet
# Only the server assembly may differ between the arms.
cmp "$out/before/TerraScale.DotNet.dll" "$out/after/TerraScale.DotNet.dll"
cmp "$out/before/TerraBase.E2E.dll" "$out/after/TerraBase.E2E.dll"
cmp "$out/before/TerraBase.E2E.runtimeconfig.json" "$out/after/TerraBase.E2E.runtimeconfig.json"
if cmp -s "$out/before/terrabase.dll" "$out/after/terrabase.dll"; then echo 'Server artifacts did not change.'; exit 1; fi
# Same file set; every file that differs is the server assembly or its symbols/docs.
diff <(cut -c67- "$out/before.all.sha256") <(cut -c67- "$out/after.all.sha256")
diff "$out/before.all.sha256" "$out/after.all.sha256" > "$out/binary-diff.txt" || true
test "$(grep '^[<>]' "$out/binary-diff.txt" | cut -c69- | sort -u | grep -vE '^\./terrabase\.(dll|pdb|xml)$' || true)" = ''
dotnet build-server shutdown > "$out/build-server-shutdown.log" 2>&1 || true
cp benchmark/scripts/api-cpu-observer.py "$out/"
lscpu -e > "$out/cpu-topology.txt"
dotnet --list-runtimes > "$out/runtimes.txt"

# Builds are complete. Reserve the other admitted heavy slots without stopping work.
reserve_fds=()
for slot in 1 2 3 4; do
  exec {reserve_fd}>"/tmp/ts-heavy-$slot.lock"
  if flock -n "$reserve_fd"; then reserve_fds+=("$reserve_fd"); fi
done
if [[ ${#reserve_fds[@]} != 3 ]]; then
  echo 'Quiet reservation unavailable; no timed arm started.'; exit 73
fi
echo "Reserved ${#reserve_fds[@]} additional heavy slots for the timed arms." > "$out/reservation.txt"
python3 -I -u "$out/api-cpu-observer.py" "$$" > "$out/cpu-processes.jsonl" &
observer=$!
vmstat -t 1 > "$out/vmstat.txt" &
vmstat=$!
trap 'kill "$observer" "$vmstat" 2>/dev/null || true; git checkout -q HEAD -- "${changed[@]}"' EXIT

for ((repeat = 1; repeat <= pairs; repeat++)); do
  arms=(before after)
  if ((repeat % 2 == 0)); then arms=(after before); fi
  for arm in "${arms[@]}"; do
    prefix=$out/$arm-$repeat
    (cd "$out/$arm" && sha256sum --check --quiet "$out/$arm.sha256" && sha256sum --check --quiet "$out/$arm.all.sha256")
    echo "$arm-$repeat" >> "$out/order.txt"
    { date -u +%FT%TZ; uptime; cat /proc/pressure/cpu; } > "$prefix.host"
    date -u +%FT%T.%NZ > "$prefix.window"
    TERRABASE_VS_TURSO_LANES=api TERRABASE_VS_TURSO_WORKLOADS=read TERRABASE_VS_TURSO_CONCURRENCY=1,16 TERRABASE_VS_TURSO_SECONDS=5 \
      TERRABASE_VS_TURSO_REPEATS=1 TERRABASE_VS_TURSO_OUT="$prefix.md" taskset -c "$cpus" dotnet exec "$out/$arm/TerraBase.E2E.dll" \
      --treenode-filter '/*/*/VsTursoBenchmark/*' --minimum-expected-tests 1 > "$prefix.log" 2>&1 || { tail -60 "$prefix.log"; exit 1; }
    date -u +%FT%T.%NZ >> "$prefix.window"
    { date -u +%FT%TZ; uptime; cat /proc/pressure/cpu; } >> "$prefix.host"
    echo "$arm pair $repeat completed."
  done
done
kill "$observer" "$vmstat" 2>/dev/null || true
python3 -I benchmark/scripts/api-cached-system-open-summary.py "$out" "$pairs" > "$out/summary.md"
(cd "$out" && sha256sum summary.md cpu-processes.jsonl vmstat.txt order.txt ./*-[0-9].md ./*-[0-9].log before.sha256 after.sha256 \
  before.all.sha256 after.all.sha256 > artifacts.sha256)
echo "All $((2 * pairs)) arms completed."
