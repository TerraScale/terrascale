#!/usr/bin/env bash
# Repeat-only recheck of the ClientCors span matcher's small/c16 tail (run b1 of api-cors-span-20261028.sh).
# No build and no tests: copies the frozen b1 before/after binaries, checks them against the pinned hashes before every arm,
# then runs six alternating pairs (B A, A B, B A, A B, B A, A B) under the same quiet reservation, observer and env as b1.
# The assessment is predeclared in api-cors-span-repeat-summary.py. Nothing is dropped or retried; a failing arm stops the run.
# Run through: TS_HEAVY_HOST=beta ts-heavy-any nice -n 19 benchmark/scripts/api-cors-span-repeat-20261028.sh <label> --repo terrascale --branch <branch>
cd -- "$(dirname -- "$0")/../.."
set -euo pipefail
label=${1:?usage: api-cors-span-repeat-20261028.sh <fresh run label>}
[[ "$label" =~ ^[A-Za-z0-9][A-Za-z0-9._-]*$ ]] || { echo 'Invalid run label.' >&2; exit 2; }
frozen=$HOME/remote-builds/terrascale/fix-api-cors-span-path-dd6840fd8f6c928f24b43da9d2af2868068850ab70fecedbaed280f5f4c6ceb8/benchmark/bench/cors-span-b1
cpus=2-11
pairs=6
out=$PWD/benchmark/bench/cors-span-repeat-$label
mkdir "$out"
exec > "$out/job.log" 2>&1 < /dev/null
git rev-parse HEAD > "$out/script-sha.txt"
echo "$frozen" > "$out/frozen-source.txt"

# The pinned b1 binaries; only terrabase.dll differs between the arms.
printf '%s  %s\n' \
  4d83e14a7d674f3541520b31de8b7893e805edaff62f15bf2f157f64d2baa925 terrabase.dll \
  a4f3be7fccbf55ff5b5516cb5d0063982a67771fd47c2d45fe7cb982e641e19a TerraScale.DotNet.dll \
  f871c1bdf75630d1e0fae1ffe8798bee6bcc10bac447e0379aeab356b80ad33d TerraBase.E2E.dll \
  5628e9d54e2eae59b08c162e41eedb7bffd95ab6b2646c23a8d49473f590809a TerraBase.E2E.runtimeconfig.json > "$out/before.sha256"
sed 's/^4d83e14a7d674f3541520b31de8b7893e805edaff62f15bf2f157f64d2baa925 /0101b702b325af34f0cb84aa10032fd1ab815993175803a8f83b4a1862f3aa1d /' \
  "$out/before.sha256" > "$out/after.sha256"
for arm in before after; do
  cmp "$out/$arm.sha256" "$frozen/$arm.sha256"
  (cd "$frozen/$arm" && sha256sum --check --quiet "$out/$arm.sha256")
  # Read-only use of the originals: the arms run from byte-identical copies of every file.
  cp -a "$frozen/$arm" "$out/$arm"
  diff -r "$frozen/$arm" "$out/$arm"
  (cd "$out/$arm" && find . -type f -print0 | sort -z | xargs -0 sha256sum) > "$out/$arm.all.sha256"
done
cp benchmark/scripts/api-cpu-observer.py "$out/"
lscpu -e > "$out/cpu-topology.txt"
dotnet --list-runtimes > "$out/runtimes.txt"

# Reserve the other admitted heavy slots without stopping work.
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
python3 -I benchmark/scripts/api-cors-span-repeat-summary.py "$out" "$pairs" "$frozen" > "$out/summary.md"
echo "All $((2 * pairs)) arms completed."
