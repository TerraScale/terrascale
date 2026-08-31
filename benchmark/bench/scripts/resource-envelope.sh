# The fixed benchmark resource envelope (owner decision 2026-08-31):
# every MEASURED benchmark subject — the TerraBase member harness, the
# PostgreSQL / pgbouncer / pgrest stack, RavenDB — runs capped at 8 CPUs
# and 8 GiB RAM, so like-for-like comparisons hold on any host and the
# beat-PostgreSQL claim is honest about the hardware envelope it was won
# in. Load generators (the bench lane process, the Go adapter) are NOT
# capped: they are the instrument, not the subject.
#
# Usage (source this file):
#   source "$(dirname "$0")/resource-envelope.sh"
#   envelope_wrap <command...>     # run one native subject under the envelope
#   envelope_podman <name>         # cap one already-running podman subject
#   $TERRABASE_BENCH_ENVELOPE_SPEC # "8cpu/8GiB" — stamp into result metadata
#
# Overrides: TERRABASE_BENCH_ENVELOPE_CPUS (default 8),
# TERRABASE_BENCH_ENVELOPE_MEMORY (default 8G, systemd suffix form).

TERRABASE_BENCH_ENVELOPE_CPUS="${TERRABASE_BENCH_ENVELOPE_CPUS:-8}"
TERRABASE_BENCH_ENVELOPE_MEMORY="${TERRABASE_BENCH_ENVELOPE_MEMORY:-8G}"
export TERRABASE_BENCH_ENVELOPE_SPEC="${TERRABASE_BENCH_ENVELOPE_CPUS}cpu/${TERRABASE_BENCH_ENVELOPE_MEMORY}"

# The fleet-recorded method (GOAL.md decision log, 2026-08-31):
# taskset -c 0-7 for CPU affinity — children inherit it and
# available_parallelism follows affinity, so the subject and every
# runtime it spawns see exactly the envelope — wrapped in a
# systemd-run user scope for the 8-GiB memory ceiling.
envelope_wrap() {
  if command -v systemd-run >/dev/null 2>&1 && command -v taskset >/dev/null 2>&1; then
    systemd-run --user --scope --quiet \
      -p "MemoryMax=${TERRABASE_BENCH_ENVELOPE_MEMORY}" \
      -p TasksMax=4096 \
      -- taskset -c "0-$((TERRABASE_BENCH_ENVELOPE_CPUS - 1))" "$@"
  elif command -v taskset >/dev/null 2>&1; then
    echo "resource-envelope: systemd-run unavailable; applying CPU affinity only" >&2
    taskset -c "0-$((TERRABASE_BENCH_ENVELOPE_CPUS - 1))" "$@"
  else
    echo "resource-envelope: neither systemd-run nor taskset available; running unconstrained" >&2
    "$@"
  fi
}

# Podman exposes per-container cpu/memory limits at creation; for
# containers this file's callers do not own (started by another lane's
# script), cap them in place by name.
envelope_podman() {
  local name="$1"
  if command -v podman >/dev/null 2>&1; then
    podman update \
      --cpus "$TERRABASE_BENCH_ENVELOPE_CPUS" \
      --memory "$TERRABASE_BENCH_ENVELOPE_MEMORY" \
      "$name" >/dev/null
  else
    echo "resource-envelope: podman unavailable; container $name left unconstrained" >&2
  fi
}
