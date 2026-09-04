 # E-13 measured-subject resource contract: one launch member, 2 OCPU / 12
 # GiB (GOAL.md D-65). A gating run has no CPU-only or unconstrained fallback.
if [[ -n "${TERRABASE_BENCH_ENVELOPE_CPUS:-}" &&
      "$TERRABASE_BENCH_ENVELOPE_CPUS" != 2 ]]; then
  echo "resource-envelope: TERRABASE_BENCH_ENVELOPE_CPUS cannot exceed 2" >&2
  return 1 2>/dev/null || exit 1
fi
if [[ -n "${TERRABASE_BENCH_ENVELOPE_MEMORY:-}" &&
      "$TERRABASE_BENCH_ENVELOPE_MEMORY" != 12G ]]; then
  echo "resource-envelope: TERRABASE_BENCH_ENVELOPE_MEMORY cannot exceed 12G" >&2
  return 1 2>/dev/null || exit 1
fi
if [[ "${TERRABASE_BENCH_ENVELOPE:-1}" == 0 ]]; then
  echo "resource-envelope: TERRABASE_BENCH_ENVELOPE=0 is forbidden for gating runs" >&2
  return 1 2>/dev/null || exit 1
fi
readonly TERRABASE_BENCH_ENVELOPE_CPUS=2
readonly TERRABASE_BENCH_ENVELOPE_MEMORY=12G
readonly TERRABASE_BENCH_ENVELOPE_SPEC=2ocpu/12GiB
export TERRABASE_BENCH_ENVELOPE_CPUS TERRABASE_BENCH_ENVELOPE_MEMORY
export TERRABASE_BENCH_ENVELOPE_SPEC

if [[ -n "${TERRABASE_BENCH_ENVELOPE_CPUS_OVERRIDE:-}" &&
      "$TERRABASE_BENCH_ENVELOPE_CPUS_OVERRIDE" != 2 ]]; then
  echo "resource-envelope: CPU override violates the 2 OCPU ceiling" >&2
  return 1 2>/dev/null || exit 1
fi
if [[ -n "${TERRABASE_BENCH_ENVELOPE_MEMORY_OVERRIDE:-}" &&
      "$TERRABASE_BENCH_ENVELOPE_MEMORY_OVERRIDE" != 12G ]]; then
  echo "resource-envelope: memory override violates the 12 GiB ceiling" >&2
  return 1 2>/dev/null || exit 1
fi

envelope_user_manager_up() {
  command -v systemctl >/dev/null 2>&1 && systemctl --user show -p ActiveState >/dev/null 2>&1
}

envelope_require() {
  command -v systemd-run >/dev/null 2>&1 && command -v taskset >/dev/null 2>&1 || {
    echo "resource-envelope: systemd-run and taskset are required for a gating run" >&2
    return 1
  }
  if ! envelope_user_manager_up && ! command -v sudo >/dev/null 2>&1; then
    echo "resource-envelope: user systemd manager or sudo is required" >&2
    return 1
  fi
  if ! envelope_user_manager_up && ! sudo -n true 2>/dev/null; then
    echo "resource-envelope: user manager unavailable and sudo -n is not usable" >&2
    return 1
  fi
}

envelope_cpu_set() {
  local allowed
  allowed="$(taskset -pc $$ 2>/dev/null | sed 's/.*: //')" || return 1
  python3 - "$allowed" <<'PY'
import sys
cpus = []
for item in sys.argv[1].split(','):
    if '-' in item:
        first, last = (int(value) for value in item.split('-', 1))
        cpus.extend(range(first, last + 1))
    elif item.strip():
        cpus.append(int(item))
if len(cpus) < 2:
    raise SystemExit("resource-envelope: fewer than two CPUs are available")
print(f"{cpus[0]}-{cpus[1]}")
PY
}

envelope_wrap() {
  envelope_require
  local cpus
  cpus="$(envelope_cpu_set)" || return 1
  local systemd_run="$(command -v systemd-run)"
  local -a scope
  if envelope_user_manager_up; then
    scope=("$systemd_run" --user --scope --quiet)
    export TERRABASE_BENCH_ENVELOPE_SCOPE=user
  else
    scope=(sudo -nE "$systemd_run" --scope --quiet --uid "$(id -u)" --gid "$(id -g)")
    export TERRABASE_BENCH_ENVELOPE_SCOPE=system
  fi
  "${scope[@]}" -p MemoryMax="$TERRABASE_BENCH_ENVELOPE_MEMORY" \
    -p CPUQuota=200% -p AllowedCPUs="$cpus" -p TasksMax=4096 -- \
    taskset -c "$cpus" "$@"
}

envelope_podman() {
  local name="${1:?container name is required}"
  command -v podman >/dev/null 2>&1 || {
    echo "resource-envelope: podman is required for a gating run" >&2
    return 1
  }
  local cpus="$(envelope_cpu_set)" || return 1
  podman update --cpus 2 --memory "$TERRABASE_BENCH_ENVELOPE_MEMORY" \
    --cpuset-cpus "$cpus" "$name" >/dev/null
  local actual
  actual="$(podman inspect --format '{{.HostConfig.CpuQuota}}:{{.HostConfig.Memory}}:{{.HostConfig.CpusetCpus}}' "$name")"
  [[ "$actual" == 200000:12884901888:"$cpus" ]] || {
    echo "resource-envelope: cgroup fence verification failed ($actual)" >&2
    return 1
  }
}

envelope_subject_evidence() {
  local name="${1:?subject name is required}" pid="${2:?subject pid is required}" output="${3:?evidence output is required}"
  [[ -r "/proc/$pid/status" ]] || { echo "resource-envelope: PID $pid is gone" >&2; return 1; }
  local cgroup="$(awk -F: '$1 == 0 {print $3}' "/proc/$pid/cgroup")"
  [[ -n "$cgroup" ]] || { echo "resource-envelope: no cgroup v2 path for $name" >&2; return 1; }
  local root="/sys/fs/cgroup$cgroup"
  [[ -r "$root/memory.max" && -r "$root/cpu.max" && -r "$root/cpuset.cpus.effective" ]] || {
    echo "resource-envelope: cgroup evidence is incomplete for $name" >&2
    return 1
  }
  {
    echo "subject=$name"; echo "pid=$pid"; echo "envelope_spec=$TERRABASE_BENCH_ENVELOPE_SPEC"
    echo "cgroup=$cgroup"; echo "cpu_max=$(<"$root/cpu.max")"; echo "cpuset=$(<"$root/cpuset.cpus.effective")"
    echo "memory_max=$(<"$root/memory.max")"; echo "memory_current=$(<"$root/memory.current")"; echo "memory_peak=$(<"$root/memory.peak")"
    echo "rss_kib=$(awk '/VmRSS:/{print $2}' "/proc/$pid/status")"; echo "io_stat=$(tr '\n' ';' <"$root/io.stat" 2>/dev/null || true)"
  } >"$output"
}
