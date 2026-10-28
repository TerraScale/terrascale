#!/usr/bin/env bash
# Auth temporary allocations (ValidatedTokens.KeyOf + UserAuthenticationHandler.Success): one preregistered paired measurement of the
# keyed api read lane, protocol and rules in benchmark/bench/2026-10-28-perf-api-auth-temporary-allocation-prereg.md.
# Builds baseline 9cbc00d60's two changed files (before) and candidate 4cba57edb (after) at the same candidate HEAD/path, puts the
# candidate-compiled E2E harness in both arms and freezes every manifest. Then a 3/3 smoke on each arm and the key/token control,
# all outside timing, then six alternating fresh-process pairs (B A, A B, B A, A B, B A, A B) under the quiet reservation.
# No retries, no dropped arms; any failed check stops the run before or between arms and everything is kept.
# Run through: TS_HEAVY_HOST=delta ts-heavy-any nice -n 19 benchmark/scripts/api-auth-temporary-allocation-20261028.sh <label> --repo terrascale --branch <branch>
cd -- "$(dirname -- "$0")/../.."
set -euo pipefail
label=${1:?usage: api-auth-temporary-allocation-20261028.sh <fresh run label>}
[[ "$label" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]] || { echo 'Invalid run label.' >&2; exit 2; }
sdk_pin=36d114877a69d18fba33b27ad362bd3e68e9e7ff
baseline=9cbc00d60788ab383917cf8768879beffec4fc0c
candidate=4cba57edbe8b3bf93944455c15203678788daf15
changed=(src/TerraBase/Features/UserAuth/UserAuthenticationHandler.cs src/TerraBase/Features/UserAuth/ValidatedTokens.cs)
cpus=2-11
pairs=6
out=$PWD/benchmark/bench/auth-temporary-allocation-$label
mkdir "$out"
exec > "$out/job.log" 2>&1 < /dev/null
# No build daemon may outlive a build into the timed arms.
export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false DOTNET_CLI_USE_MSBUILD_SERVER=0
git rev-parse HEAD > "$out/harness-sha.txt"
echo "$baseline" > "$out/baseline-sha.txt"
echo "$candidate" > "$out/candidate-sha.txt"
git diff --quiet && git diff --cached --quiet
# This branch adds benchmark/ files to main only; nothing it adds is built into either arm.
git fetch -q origin "$baseline" "$candidate"
git fetch -q origin main
test "$(git diff --name-only "$(git merge-base FETCH_HEAD HEAD)" HEAD | grep -v '^benchmark/' || true)" = ''
# Candidate is baseline plus the two UserAuth files and their Fast test; every build input other than the two files is identical.
test "$(git merge-base "$baseline" "$candidate")" = "$baseline"
test "$(git diff --name-only "$baseline" "$candidate")" = "$(printf '%s\n' "${changed[0]}" "${changed[1]}" tests/TerraBase.Fast/Management/UserAuthTests.cs)"
test "$(git diff --name-only "$baseline" "$candidate" -- src Directory.Build.props Directory.Build.targets Directory.Packages.props global.json \
  NuGet.config benchmark/dotnet tests/TerraBase.E2E tests/TerraBase.TestHarness)" = "$(printf '%s\n' "${changed[@]}")"
git diff "$baseline" "$candidate" -- "${changed[@]}" > "$out/production.diff"
# Harness API compatibility, by source: neither changed type is named by the E2E, its harness or the benchmark sources.
if git grep -n -E 'ValidatedTokens|UserAuthenticationHandler' "$candidate" -- tests/TerraBase.E2E tests/TerraBase.TestHarness benchmark/dotnet; then exit 1; fi

# One candidate HEAD and build path keep Git-derived assembly/PDB stamps identical. Only the two source files change.
work=$HOME/remote-builds/terrascale-ab/auth-temporary-allocation-$label
sdk=$HOME/remote-builds/ts-dotnet/auth-temporary-allocation-$label
mkdir -p "$(dirname "$work")" "$(dirname "$sdk")"
test ! -e "$work"
test ! -e "$sdk"
printf '%s\n%s\n' "$work" "$sdk" > "$out/build-paths.txt"
git worktree add -q --detach "$work" "$candidate"
git clone --quiet --no-checkout https://github.com/TerraScale/ts-dotnet.git "$sdk"
git -C "$sdk" checkout --quiet --detach "$sdk_pin"
git -C "$sdk" rev-parse HEAD > "$out/sdk-sha.txt"
test "$(cat "$out/sdk-sha.txt")" = "$sdk_pin"
export TsDotnetRoot=$sdk
# The downloader checks pin format before download and verifies native asset hashes afterwards.
(cd "$work" && scripts/vendor/download-turso-native.sh) > "$out/turso-native.log" 2>&1
(cd "$work" && sha256sum src/vendor/Turso.Native/SHA256SUMS src/vendor/Turso.Data.Sqlite/native/runtimes/*/native/libturso_sdk_kit.so) > "$out/turso-native.sha256"
(cd "$work" && make restore) > "$out/restore.log" 2>&1 || { tail -40 "$out/restore.log"; exit 1; }

build() {
  test "$(git -C "$work" rev-parse HEAD)" = "$candidate"
  if [[ "$1" == before ]]; then
    test "$(git -C "$work" status --porcelain --untracked-files=no)" = "$(printf 'M  %s\n' "${changed[@]}")"
  else
    test -z "$(git -C "$work" status --porcelain --untracked-files=no)"
  fi
  for source in "${changed[@]}"; do cmp <(git -C "$work" show "$2:$source") "$work/$source"; done
  (cd "$work" && sha256sum "${changed[@]}") > "$out/$1-source.sha256"
  (cd "$work" && dotnet build tests/TerraBase.E2E --configuration Release --no-restore) > "$out/build-$1.log" 2>&1 || { tail -60 "$out/build-$1.log"; return 1; }
  grep -E '^ *[0-9]+ Warning\(s\)|^ *[0-9]+ Error\(s\)' "$out/build-$1.log" > "$out/build-$1.counts" || true
  mkdir "$out/$1"
  cp -a "$work/tests/TerraBase.E2E/bin/Release/net11.0/." "$out/$1/"
  rm -rf "$out/$1/TestResults"
  config=$out/$1/TerraBase.E2E.runtimeconfig.json
  # The established benchmark runtime: workstation concurrent GC with TieredPGO, the same in both arms.
  jq '.runtimeOptions.configProperties |= (del(."Microsoft.Extensions.DependencyInjection.DisableDynamicEngine")
    + {"System.GC.Server":false,"System.GC.Concurrent":true,"System.Runtime.TieredPGO":true})' "$config" > "$config.tmp"
  mv "$config.tmp" "$config"
  cp "$out/$1/TerraBase.E2E.dll" "$out/$1-own-TerraBase.E2E.dll"
  cp "$out/$1/TerraBase.E2E.pdb" "$out/$1-own-TerraBase.E2E.pdb"
}
git -C "$work" checkout -q "$baseline" -- "${changed[@]}"
build before "$baseline"
git -C "$work" checkout -q "$candidate" -- "${changed[@]}"
build after "$candidate"
dotnet build-server shutdown > "$out/build-server-shutdown.log" 2>&1 || true
# The common harness: the candidate-compiled E2E DLL/PDB in both arms. Each arm keeps its own terrabase.dll/pdb.
cp "$out/after/TerraBase.E2E.dll" "$out/before/TerraBase.E2E.dll"
cp "$out/after/TerraBase.E2E.pdb" "$out/before/TerraBase.E2E.pdb"
sha256sum "$out"/{before,after}-own-TerraBase.E2E.{dll,pdb} > "$out/own-harness.sha256"
# Freeze. Only TUnit's TestResults/ (rewritten on every run) is outside the all-files manifest.
for arm in before after; do
  (cd "$out/$arm" && sha256sum terrabase.dll terrabase.pdb TerraScale.DotNet.dll TerraBase.E2E.dll TerraBase.E2E.pdb TerraBase.E2E.runtimeconfig.json) > "$out/$arm.sha256"
  (cd "$out/$arm" && find . -type f -not -path './TestResults/*' -print0 | sort -z | xargs -0 sha256sum) > "$out/$arm.all.sha256"
done
same() {
  cmp "$out/before/TerraScale.DotNet.dll" "$out/after/TerraScale.DotNet.dll"
  cmp "$out/before/TerraBase.E2E.dll" "$out/after/TerraBase.E2E.dll"
  cmp "$out/before/TerraBase.E2E.pdb" "$out/after/TerraBase.E2E.pdb"
  cmp "$out/before/TerraBase.E2E.runtimeconfig.json" "$out/after/TerraBase.E2E.runtimeconfig.json"
  for arm in before after; do
    (cd "$out/$arm" && sha256sum --check --quiet "$out/$arm.sha256" && sha256sum --check --quiet "$out/$arm.all.sha256" &&
      diff <(cut -c67- "$out/$arm.all.sha256") <(find . -type f -not -path './TestResults/*' -print | sort))
  done
}
same
if cmp -s "$out/before/terrabase.dll" "$out/after/terrabase.dll"; then echo 'Server artifacts did not change.'; exit 1; fi
# Same file set; the only differing files are the server's DLL and PDB. Anything else stops the run.
diff <(cut -c67- "$out/before.all.sha256") <(cut -c67- "$out/after.all.sha256")
diff "$out/before.all.sha256" "$out/after.all.sha256" > "$out/binary-diff.txt" || true
test "$(grep '^[<>]' "$out/binary-diff.txt" | cut -c69- | sort -u)" = "$(printf '%s\n' ./terrabase.dll ./terrabase.pdb)"

# Compatibility smoke outside timing, no retries: the real-HTTP local replication metric scenario, 3 cases on each arm.
for arm in before after; do
  taskset -c "$cpus" dotnet exec "$out/$arm/TerraBase.E2E.dll" \
    --treenode-filter '/*/*/RepairAndContractE2ETests/Every_region_publishes_the_replication_lag_series*' \
    --minimum-expected-tests 3 --no-ansi > "$out/smoke-$arm.log" 2>&1 || { tail -60 "$out/smoke-$arm.log"; exit 1; }
  for field in 'total: 3' 'succeeded: 3' 'failed: 0' 'skipped: 0'; do
    test "$(grep -Ec "^[[:space:]]*$field[[:space:]]*$" "$out/smoke-$arm.log")" = 1
  done
done
same
# Key equivalence and token shape (api-auth-token-key-control.cs) outside timing, from a directory no Directory.Build.* reaches.
control=$(mktemp -d)
cp benchmark/scripts/api-auth-token-key-control.cs "$work/global.json" "$control/"
(cd "$control" && dotnet run api-auth-token-key-control.cs -- "$out/before" "$out/after") > "$out/token-key-control.md" 2> "$out/token-key-control.err" \
  || { cat "$out/token-key-control.err"; tail -20 "$out/token-key-control.md"; exit 1; }
dotnet build-server shutdown >> "$out/build-server-shutdown.log" 2>&1 || true
same
cp benchmark/scripts/api-cpu-observer.py "$out/"
lscpu -e > "$out/cpu-topology.txt"
dotnet --list-runtimes > "$out/runtimes.txt"

# Builds and checks are complete. Reserve the three other admitted heavy slots without stopping work.
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
trap 'kill "$observer" "$vmstat" 2>/dev/null || true' EXIT

for ((repeat = 1; repeat <= pairs; repeat++)); do
  arms=(before after)
  if ((repeat % 2 == 0)); then arms=(after before); fi
  for arm in "${arms[@]}"; do
    prefix=$out/$arm-$repeat
    same
    # A fresh process per arm: nothing from an earlier arm, smoke or control may still run.
    if pgrep -a -f -- "^([^[:space:]]*/)?dotnet (exec $out/(before|after)/TerraBase[.]E2E[.]dll( |$)|run api-auth-token-key-control[.]cs( |$))"; then exit 1; fi
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
same
python3 -I benchmark/scripts/api-auth-temporary-allocation-summary.py "$out" "$pairs" > "$out/summary.md"
(cd "$out" && sha256sum summary.md cpu-processes.jsonl vmstat.txt order.txt ./*-[0-9].md ./*-[0-9].log before.sha256 after.sha256 \
  before.all.sha256 after.all.sha256 own-harness.sha256 smoke-before.log smoke-after.log token-key-control.md production.diff > artifacts.sha256)
echo "All $((2 * pairs)) arms completed."
