#!/usr/bin/env bash
# Revised protocol: one common frozen E2E harness against the two frozen r1 server binaries.
# r1 stopped before timing because its separately built E2E DLLs differed. Preserve r1 untouched;
# this is a new run, not a retry under r1's protocol. No build or identity-field normalization.
# Copy the candidate E2E DLL/PDB into both new arm directories, require all non-server files identical,
# and pass the existing real-HTTP local replication metric scenario against both before timing.
# Then six alternating fresh-process pairs (B A, A B, B A, A B, B A, A B); no retries or discarded arms.
# Timing and assessment rules remain those predeclared in api-cached-system-open-summary.py.
# Run through: TS_HEAVY_HOST=beta ts-heavy-any nice -n 19 benchmark/scripts/api-cached-system-open-20261028.sh <fresh label> --repo terrascale --branch <branch>
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
git rev-parse HEAD > "$out/harness-sha.txt"
echo "$baseline" > "$out/baseline-sha.txt"
echo "$candidate" > "$out/candidate-sha.txt"
git merge-base --is-ancestor "$baseline" HEAD
git merge-base --is-ancestor "$candidate" HEAD
test "$(git diff --name-only "$candidate" HEAD | grep -v '^benchmark/scripts/' || true)" = ''
test "$(git diff --name-only "$baseline" HEAD -- src Directory.Build.props Directory.Build.targets Directory.Packages.props global.json \
  NuGet.config benchmark/dotnet tests/TerraBase.E2E tests/TerraBase.TestHarness)" = "$(printf '%s\n' "${changed[@]}")"
git diff "$baseline" HEAD -- "${changed[@]}" > "$out/production.diff"
git diff --quiet && git diff --cached --quiet
# r1's exact frozen artifacts; all checks and runs below operate on new copies only.
frozen=$HOME/remote-builds/terrascale/fix-terrabase-cached-system-open-ab-425dad0ed56916cf56224d19eb2e76fc3465d797f40393a72b60c1d7f7ed1e48/benchmark/bench/cached-system-open-r1
printf '%s\n' "$frozen" > "$out/frozen-source.txt"
test "$(cat "$frozen/sdk-sha.txt")" = "$sdk_pin"
printf '%s  %s\n' \
  54e1578ce84105e9f54b836d30470fed6eed58375fb9a5188f1e6b6609cd74af "$frozen/before/terrabase.dll" \
  6868fb0b561c7a24fcbecaed5c5a3a708a5c3aab20a066dc8ab7c96459476207 "$frozen/after/terrabase.dll" \
  a28fac0aa00b7747328fe4322f731717dab1e0a18b0938ea16a0f07adbaf5e2d "$frozen/before/TerraBase.E2E.dll" \
  2eadb593cea6a3056e081a06bcbc3476fbe19a40f40a53db1eeb289c4331651e "$frozen/after/TerraBase.E2E.dll" \
  > "$out/frozen-key.sha256"
sha256sum --check "$out/frozen-key.sha256"
for arm in before after; do
  (cd "$frozen/$arm" && sha256sum --check --quiet "$frozen/$arm.all.sha256")
  cp -a "$frozen/$arm" "$out/$arm"
  cp "$frozen/$arm.all.sha256" "$out/frozen-$arm.all.sha256"
  # One harness, compiled against the candidate's reference assembly, in both arms.
  cp "$frozen/after/TerraBase.E2E.dll" "$out/$arm/TerraBase.E2E.dll"
  cp "$frozen/after/TerraBase.E2E.pdb" "$out/$arm/TerraBase.E2E.pdb"
  (cd "$out/$arm" && sha256sum terrabase.dll TerraScale.DotNet.dll TerraBase.E2E.dll TerraBase.E2E.runtimeconfig.json) > "$out/$arm.sha256"
  (cd "$out/$arm" && find . -type f -not -path './TestResults/*' -print0 | sort -z | xargs -0 sha256sum) > "$out/$arm.all.sha256"
done
cmp "$out/before/TerraScale.DotNet.dll" "$out/after/TerraScale.DotNet.dll"
cmp "$out/before/TerraBase.E2E.dll" "$out/after/TerraBase.E2E.dll"
cmp "$out/before/TerraBase.E2E.runtimeconfig.json" "$out/after/TerraBase.E2E.runtimeconfig.json"
if cmp -s "$out/before/terrabase.dll" "$out/after/terrabase.dll"; then echo 'Server artifacts did not change.'; exit 1; fi
diff <(cut -c67- "$out/before.all.sha256") <(cut -c67- "$out/after.all.sha256")
diff "$out/before.all.sha256" "$out/after.all.sha256" > "$out/binary-diff.txt" || true
test "$(grep '^[<>]' "$out/binary-diff.txt" | cut -c69- | sort -u | grep -vE '^\./terrabase\.(dll|pdb|xml)$' || true)" = ''
# Real Program/Kestrel, local nodes regardless of live environment: the existing scenario seeds
# documents over HTTP, waits for convergence and reads metrics over HTTP (three isolated cases).
# These are compatibility checks outside timing; both must pass, no retries.
for arm in before after; do
  taskset -c "$cpus" dotnet exec "$out/$arm/TerraBase.E2E.dll" \
    --treenode-filter '/*/*/RepairAndContractE2ETests/Every_region_publishes_the_replication_lag_series*' \
    --minimum-expected-tests 3 > "$out/smoke-$arm.log" 2>&1 || { tail -60 "$out/smoke-$arm.log"; exit 1; }
  (cd "$out/$arm" && sha256sum --check --quiet "$out/$arm.all.sha256")
done
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
trap 'kill "$observer" "$vmstat" 2>/dev/null || true' EXIT

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
  before.all.sha256 after.all.sha256 frozen-key.sha256 smoke-before.log smoke-after.log > artifacts.sha256)
echo "All $((2 * pairs)) arms completed."
