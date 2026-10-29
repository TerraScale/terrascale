# External process CPU observer

`python3 -I -u benchmark/scripts/api-cpu-observer.py <owned-root-pid>` emits one JSON record per second on Linux. Redirect stdout to a fresh file in `benchmark/bench/` and stop the observer after the owned workload finishes. Run the workload through `ts-heavy-any`; the observer does not reserve a host or launch a benchmark.

Each interval separates CPU consumed by the root and its descendants from other visible processes, and reports the five busiest external process names and PIDs. Percentages use one logical CPU as 100%, so totals can exceed 100%. Newly observed PIDs contribute only after two snapshots; PID reuse is checked against process start time. Exited processes and short-lived work between snapshots can be missed. An observed quiet interval alone does not certify uncontended performance.

This retains the reusable observer from the API profiling experiments. Frozen binary launchers and dated measurement reports are not required to use it.
