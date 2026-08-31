# Ambient IO-pressure snapshot for benchmark run metadata (2026-08-31).
#
# The resource envelope fences CPU (affinity) and RAM (cgroup memory
# ceiling) but CANNOT fence the disk: a fleet-mate saturating the shared
# device turns every fsync into O(100ms), and durability-bound write
# lanes (synchronous FULL, one fsync per group-commit batch) measure the
# churn, not the engine. Stamp this line into every retained result's
# metadata next to loadavg so a write-family number can be read honestly.
#
# Usage:  bench/scripts/io-pressure.sh [device-glob]   (default: the
#         device backing the current directory, else all block devices)
# Output: one line, "io-pressure util=<%> await-<dev>=<ms> ..." sampled
#         over one second from /sys/block + /proc/diskstats.

set -euo pipefail
SAMPLE_SECONDS="${IO_PRESSURE_SAMPLE_SECONDS:-1}"

# Resolve the backing device of the current directory when possible.
BACKING=""
if command -v df >/dev/null 2>&1; then
  BACKING="$(df --output=source . 2>/dev/null | tail -1 | sed 's#^/dev/##')"
fi

read_stats() {
  local dev="$1"
  awk '{print $1+0, $3+0, $7+0}' "/sys/block/$dev/stat" 2>/dev/null || true
}

DEVS="$(ls /sys/block 2>/dev/null | grep -Ev '^(loop|ram|zram|sr)' | head -8)"
declare -A BEFORE_IO BEFORE_MS AFTER_IO AFTER_MS
for dev in $DEVS; do
  read -r io _ ms _ <<<"$(read_stats "$dev" | awk '{print $1, $2, $3}')"
  BEFORE_IO[$dev]=$io
  BEFORE_MS[$dev]=$ms
done

sleep "$SAMPLE_SECONDS"

OUT="io-pressure"
for dev in $DEVS; do
  read -r io _ ms _ <<<"$(read_stats "$dev" | awk '{print $1, $2, $3}')"
  AFTER_IO[$dev]=$io
  AFTER_MS[$dev]=$ms
  ios=$(( ${AFTER_IO[$dev]:-0} - ${BEFORE_IO[$dev]:-0} ))
  mss=$(( ${AFTER_MS[$dev]:-0} - ${BEFORE_MS[$dev]:-0} ))
  if (( ios > 0 )); then
    util=$(( ios * 100 / SAMPLE_SECONDS / 1000 )) # ops/ms→% of ms-busy
    await=$(( mss > 0 && ios > 0 ? mss / ios : 0 ))
    marker=""
    [[ "$dev" == "$BACKING" ]] && marker="*"
    OUT="$OUT ${dev}${marker}:util~${util}%await=${await}ms"
  fi
done
echo "$OUT"
