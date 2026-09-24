# cgroup v2 resource gate for the 40K tiered RustFS qualification.
#
# memory.peak is deliberately reported rather than used as a hard gate. Linux
# cgroup v2 can briefly report memory.peak above memory.max while reclaim is
# resolving. The final memory.current must be back within the configured cap;
# swap and OOM counters remain hard gates.

def memory_events:
  ((. // "")
   | split("\n")
   | map(select(length > 0)
         | capture("^(?<name>[^[:space:]]+)[[:space:]]+(?<value>[0-9]+)$")
         | {(.name): (.value | tonumber)})
   | add // {});

.FinalCgroup as $cgroup
| ($cgroup.MemoryMax | tonumber) as $memory_max
| ($cgroup.MemoryPeak | tonumber) as $memory_peak
| ($cgroup.MemoryCurrent | tonumber) as $memory_current
| ($cgroup.SwapMax | tonumber) as $swap_max
| ($cgroup.SwapCurrent | tonumber) as $swap_current
| ($cgroup.MemoryEvents | memory_events) as $events
| select(
    $memory_max == $configured_max
    and $memory_current <= $memory_max
    and $swap_max == 0
    and $swap_current == 0
    and ($events | has("max"))
    and $events.oom == 0
    and $events.oom_kill == 0
    and $events.oom_group_kill == 0
  )
| {
    verification: "passed",
    policy: {
      configuredMemoryMaxBytes: $configured_max,
      finalMemoryCurrentMustBeAtMostMax: true,
      memoryPeakMustBeAtMostMax: false,
      peakNote: "memory.peak is reported as pressure evidence; cgroup v2 permits brief reclaim overshoot, while final memory.current must be within memory.max",
      zeroSwapRequired: true,
      zeroOomCountersRequired: true
    },
    memory: {
      configuredMaxBytes: $memory_max,
      finalCurrentBytes: $memory_current,
      peakBytes: $memory_peak,
      peakExceedsMax: ($memory_peak > $memory_max),
      peakOvershootBytes: (if $memory_peak > $memory_max then $memory_peak - $memory_max else 0 end)
    },
    memoryEvents: $events,
    pressure: {
      memoryEventsMax: $events.max,
      note: "memory.events.max records cgroup limit pressure; it does not waive the final-current, swap, or OOM gates"
    }
  }
