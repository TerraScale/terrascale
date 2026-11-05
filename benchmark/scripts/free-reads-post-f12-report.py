import json
import statistics
import sys
from pathlib import Path
from datetime import datetime


def records(path):
    return [json.loads(line) for line in Path(path).read_text().splitlines() if line.strip()]


def span(values, digits=0):
    values = [value for value in values if value is not None]
    if not values:
        return "—"
    render = lambda value: f"{value:,.{digits}f}"
    low, high = min(values), max(values)
    center = statistics.median(values)
    return render(center) if low == high else f"{render(center)} ({render(low)}–{render(high)})"


def origin(row):
    return sum(value for key, value in row["requests"].items() if key not in {"cdn", "hints"})


def full(row):
    return row.get("outcome", "complete") == "complete" and all(row[phase]["count"] == row["readers"] for phase in ["snapshot", "catchup", "tail"])


rows, before, analytics, deployment = map(records, sys.argv[1:5])
cells = [row for row in rows if row.get("type") == "cell"]
previous = [row for row in before if row.get("type") == "cell"]
audit = next(row for row in before if row.get("type") == "cpu_capacity_audit")
factor = audit["reported_processors"] / audit["quota_cores"]
lookup = {(row["region"], row["shape"], row["readers"], row["repeat"]): row for row in analytics if row.get("type") == "r2"}
methods = {(row["sha"], row["host"], row["node"]): row for row in rows if row.get("type") == "method"}
capacity = [row for row in rows if row.get("type") == "cpu_capacity"]
stops = records(Path(sys.argv[1]).with_name("2026-11-04-f10-post-f12-stops.jsonl"))
baseline = next(row for row in stops if row.get("type") == "baseline_report")
release = next(row for row in stops if row.get("type") == "resume" and row.get("fresh_full_matrix"))
groups = []
for region in ["gru", "sjc", "fra"]:
    for shape in ["table", "where_columns"]:
        for readers in [1, 10, 100, 1000]:
            key = region, shape, readers
            group = sorted((row for row in cells if (row["region"], row["shape"], row["readers"]) == key), key=lambda row: row["repeat"])
            old = [row for row in previous if (row["region"], row["shape"], row["readers"]) == key]
            groups.append((region, shape, readers, group, old))
matrix = len(cells) == 72 and all([row["repeat"] for row in group] == [1, 2, 3] and all(full(row) for row in group) for _, _, _, group, _ in groups)
r2_available = sum(row.get("status") == "available" and row.get("get_object") is not None for row in lookup.values())
safe = bool(capacity) and len({(row.get("pid"), row.get("start_ticks")) for row in capacity}) == 1 and all(row.get("pid") == baseline["pid"] for row in capacity) and all(datetime.fromisoformat(row["start"]) >= datetime.fromisoformat(release["at"]) for row in cells) and all(row["cores"] == 2 for row in capacity) and all(row["cpu_samples"] > 0 and row["cpu_max_percent"] is not None and row["cpu_max_percent"] <= 70 for row in cells) and not any(row.get("type") in {"guard", "failure"} for row in rows)
metered = bool(cells) and all(row["sql_read_units"] == 0 and row["billed_read_units_after"] == 0 and row["usage_reads_before"] == row["usage_reads_after"] for row in cells)
cdn = bool(cells) and all(row["requests"]["cdn"] >= 2 and row["requests"]["snapshot_payload"] == row["requests"]["catchup_payload"] == 0 for row in cells)
qualified = matrix and safe and metered and cdn and r2_available == 72
print("# F10 after F12: SQL shape listeners — 2026-11-04\n")
print(f"Qualification: {'complete' if qualified else 'incomplete'}. Measured cohorts: {len(cells)}/72. Full snapshot/catch-up/live-update delivery: {sum(full(row) for row in cells)}/72. R2 receipts: {r2_available}/72. Two-CPU guard: {'passed' if safe else 'unqualified'}. Sealed snapshot/catch-up CDN delivery: {'passed' if cdn else 'unqualified'}.\n")
if cells:
    print(f"The {len(cells)} measured cohorts delivered {sum(row['tail']['count'] for row in cells):,} listener updates. The highest sampled GRU TerraBase CPU within a measured cohort was {max(row['cpu_max_percent'] for row in cells if row['cpu_max_percent'] is not None):.1f}% of its two-CPU quota.\n")
if cells:
    print(f"Total origin attempts ranged from {min(origin(row) for row in cells)} to {max(origin(row) for row in cells)} per cohort; billed read component and SQL read deltas were {'zero throughout' if metered else 'not qualified'}. Each cohort's full request and latency measurements appear below.\n")
for region, shape, readers, group, old in groups:
    if (region, shape, readers) == ("gru", "where_columns", 100) and len(group) == len(old) == 3:
        print(f"At GRU filtered N=100, origin attempts changed from {span([origin(row) for row in old])} to {span([origin(row) for row in group])}; maximum sampled CPU changed from {max(row['cpu_max_percent'] * factor for row in old):.1f}% (conditional two-CPU correction) to {max(row['cpu_max_percent'] for row in group):.1f}% of two CPUs. Median live-update p50 changed from {statistics.median(row['tail']['p50_ms'] for row in old):.1f} ms to {statistics.median(row['tail']['p50_ms'] for row in group):.1f} ms. The request reduction and latency difference include SDK sharing and the transport change, with shared-host and tunnel effects.\n")
late_failures = [row for row in stops if row.get("type") == "failure" and row.get("error") == "LIVE_NOT_READY"]
if late_failures:
    print(f"Additional failed live-readiness attempts after baseline release: {len(late_failures)}, retained in the stop receipts. Their incomplete timings are unavailable; the three-repeat tables describe completed cohorts and do not establish a 100% attempt success rate.\n")
print("## Method and comparison boundary\n")
print("- Both table and `where active = 1` / `columns id,version` shapes; N = 1, 10, 100 and 1,000 concurrent listeners, three measured repeats per region/shape/N. Each cohort starts with a fresh table and cold sealed-object URLs: 16 rows with a 128-byte body, eight matching filtered rows, one pre-sealed catch-up change and one measured live update.")
print("- F6 sealed-chunk contract references are `edd4f9c8c` and `3baf6b501`; the CDN-capable JS SDK reader shipped in `37d7e4bb99a7db2178ede8d3e530de6e17c3e558`. Snapshot and pre-sealed catch-up payloads follow manifest URLs through Cloudflare. Authenticated discovery and the live tail retain origin work.")
print("- The reader calls the checked-out JavaScript SDK's `shape().subscribe()` in Node.js. F12 shares identical subscriptions on one authenticated SDK transport. N counts listeners in one SDK client; it does not count independent processes, clients, browser sessions or upstream sockets. Before F12, the same one-client driver created an upstream stream for every listener. The before/after comparison therefore includes SDK sharing as well as server coalescing, CDN hints and admission changes. It cannot isolate server coalescing or establish independent-client scaling.")
print("- Jobs run through `ts-heavy-any` on shared dev hosts. A temporary loopback SSH CONNECT proxy in each serving region selects CDN egress; observed CF-Ray locations are tabulated below, including any routing spillover. Snapshot and catch-up latency run from subscription creation to the corresponding materialized callback. Live-update latency runs from before the write request to the updated callback. These include dev scheduling, SSH transit, SDK controls and writer latency; they are not native regional browser or CDN-only latency.")
print("- The driver requires live-tail readiness within one second after all catch-up callbacks, inside an overall 180-second cohort deadline. A readiness failure stops the attempt and is reported separately; it is not a measured 180-second delivery timeout. Each cohort holds its established live tail idle for two seconds before the measured write. The hold contributes CPU observations and request counts, but is outside live-update latency. The earlier run had no such hold. Percentiles are nearest-rank within each repeat; latency tables show the median and min–max across three repeats. N=1 has one sample per repeat and supplies no statistically useful p99 estimate.")
print("- The corrected guard samples GRU TerraBase process CPU ticks once per second, divided by the minimum of affinity and every ancestor cgroup-v2 quota. It stops all reader load above 70%, on missing telemetry, process restart or capacity change. Capacity is two CPUs with four-CPU affinity. CPU includes other concurrent service work with no idle-baseline subtraction; tables show the highest one-second interval mean across all repeats, not instantaneous peaks. Intervals are selected by their end timestamp inside the cohort, so boundary intervals can overlap setup. Earlier CPU values are multiplied by 4/2 and remain conditional because historical quotas were not recorded. The separate F12 72.96% cold-start sample at 23:06:02.521Z was 23 seconds after restart; the [idle investigation](2026-11-04-gru-idle-cpu.md) records it. It is not the original F10 72.48% sample from the 20:22 cohort. The original F10 audit recorded process start at 20:13:47.630Z, roughly nine minutes before that cohort, but process identity was not captured during the historical cohort. CPU changes cannot be attributed exclusively to this workload.")
print("- Total origin attempts include HTTP bootstrap, manifests, epoch keys, head/current-offset reads, live polling, payload fallback and WebSocket upgrade attempts, including retries/refusals. A current-offset read can return live rows when the sealed object is not yet available; it is not necessarily an empty control request. `new_data` hint frames are notifications, not additional requests, and are counted separately. CDN GET attempts and HIT ratio come from actual SDK fetch responses. Fresh object URLs and one shared upstream fetch can yield cold MISS responses for every cohort; this run does not measure repeated independent CDN cache hits. Setup, usage queries, metrics observations and OAuth exchanges are excluded.")
print("- R2 GetObject counts come from bucket-wide Cloudflare adaptive analytics, rounded outward to whole-second reader windows. They may include adjacent setup or unrelated traffic and are not per-object attribution. Missing analytics are unavailable rather than zero. The latest receipt per cohort supplies the table; two recent windows were queried again, and their earlier responses remain in the raw receipts. The unavailable window became available; the other continued to report only PutObject after two re-queries. A response containing other actions but no GetObject action is displayed as zero reported GetObject operations; adaptive analytics do not prove that the actual object-fetch count was zero. Billed read component is the monthly ledger's `units - writeUnits`; SQL read deltas and billing are separate from infrastructure requests and dollar costs.")
print("- This is a small workload with a sealed snapshot, one sealed catch-up change and one live change. The live change can use a third CDN object or an origin current-offset response. It does not qualify larger snapshots, longer histories, browser bootstrap, independent-client fan-out, or sustained multi-write traffic. The earlier 1,000-reader pilot timed out and supplies no qualified before value.\n")
print("## Quiet baseline and release\n")
print(f"The operator reported a quiet GRU baseline from `{baseline['start']}` to `{baseline['end']}` ({baseline['elapsed_seconds']:.2f} seconds): TerraBase mean {baseline['terrabase_mean_percent']:.3f}% and maximum interval {baseline['terrabase_max_interval_percent']:.3f}% of two CPUs; host mean {baseline['host_mean_percent']:.3f}% and maximum {baseline['host_max_interval_percent']:.3f}%. Shape reads and flights were zero, with stable PID {baseline['pid']} on `{baseline['revision']}`. The [idle investigation](2026-11-04-gru-idle-cpu.md) contains the method and interval table. This baseline was measured separately from this driver. Baseline intervals are about 30 seconds, versus one-second load samples; their maxima are not directly comparable. No baseline subtraction is applied.\n")
print("After that baseline, F12 completed its separate quiet proof before this full matrix started. The operator reported one flight per update in all six independent-socket cohorts (N=1 and N=1,000; three repeats), 3,000/3,000 hints and N=1,000 CPU maxima of 50.46/45.96/46.47% of two CPUs. Those are F12 results and are not substituted for any F10 cohort. The preceding 36 clean F10 cohorts are retained as pilot evidence and excluded from this fresh matrix.\n")
print("## Deployment and generator identity\n")
print("| Region host | Live image revision | Live image digest |")
print("| --- | --- | --- |")
for row in {row["host"]: row for row in deployment}.values():
    print(f"| {row['host']} | `{row['revision']}` | `{row['image'].rsplit('@', 1)[-1]}` |")
print("\n| Generator | Node | Source revision | Hold |")
print("| --- | --- | --- | ---: |")
for row in methods.values():
    print(f"| {row['host']} | {row['node']} | `{row['sha']}` | {row['hold_ms']} ms |")
print("\n## Observed CDN locations\n")
print("| Requested egress | CF-Ray location | Responses |")
print("| --- | --- | ---: |")
for region in ["gru", "sjc", "fra"]:
    colos = {}
    for row in cells:
        if row["region"] == region:
            for colo, count in row["colos"].items():
                colos[colo] = colos.get(colo, 0) + count
    for colo, count in sorted(colos.items()):
        print(f"| {region} | {colo} | {count} |")
print("\n## Latency (milliseconds)\n")
print("| Egress | Shape | N listeners | Complete repeats | Snapshot p50 | Snapshot p99 | Catch-up p50 | Catch-up p99 | Live update p50 | Live update p99 |")
print("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |")
for region, shape, readers, group, old in groups:
    values = [span([row[phase][metric] for row in group], 1) if all(full(row) for row in group) else "unavailable" for phase in ["snapshot", "catchup", "tail"] for metric in ["p50_ms", "p99_ms"]]
    print(f"| {region} | {shape} | {readers} | {sum(full(row) for row in group)}/{len(group)} | " + " | ".join(values) + " |")
print("\n## Origin, CDN, R2 and CPU\n")
print("| Egress | Shape | N listeners | Origin attempts | WS upgrades | Hint frames | CDN GET attempts | CDN HIT % | R2 reported GetObject r1/r2/r3 | GRU CPU max % / 2 CPUs | SQL read-unit delta | Billed read component |")
print("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | --- | ---: | ---: | ---: |")
for region, shape, readers, group, old in groups:
    r2 = [lookup.get((region, shape, readers, repeat), {}).get("get_object") for repeat in [1, 2, 3]]
    values = [span([origin(row) for row in group]), span([row["requests"].get("sockets", 0) for row in group]), span([row["requests"].get("hints", 0) for row in group]), span([row["requests"]["cdn"] for row in group]), span([100 * row["cdn_hit_ratio"] for row in group if row["cdn_hit_ratio"] is not None], 2), "/".join("—" if count is None else str(count) for count in r2), span([max((row["cpu_max_percent"] for row in group if row["cpu_max_percent"] is not None), default=None)], 1), span([row["sql_read_units"] for row in group]), span([row["billed_read_units_after"] for row in group])]
    print(f"| {region} | {shape} | {readers} | " + " | ".join(values) + " |")
print("\n## Before / after F12\n")
print("Origin attempts are medians and ranges across three repeats. CPU is the maximum across all three repeats. The baseline is the stopped partial run, with its two-core correction and safety limitations retained. Changes include client sharing and a different idle hold; they are descriptive comparisons.\n")
print("| Egress | Shape | N listeners | Origin before | Origin after | GRU CPU before % / 2 CPUs (conditional) | GRU CPU after % / 2 CPUs |")
print("| --- | --- | ---: | ---: | ---: | ---: | ---: |")
for region, shape, readers, group, old in groups:
    print(f"| {region} | {shape} | {readers} | {span([origin(row) for row in old])} | {span([origin(row) for row in group])} | {span([max((row['cpu_max_percent'] * factor for row in old if row['cpu_max_percent'] is not None), default=None)], 1)} | {span([max((row['cpu_max_percent'] for row in group if row['cpu_max_percent'] is not None), default=None)], 1)} |")
print("\n## Receipts\n")
print("[Measured cohorts](2026-11-04-f10-post-f12.jsonl), [R2 analytics](2026-11-04-f10-post-f12-r2.jsonl), [deployment identity](2026-11-04-f10-post-f12-deployment.jsonl), [stops and operator baseline](2026-11-04-f10-post-f12-stops.jsonl), [excluded pilot cohorts](2026-11-04-f10-post-f12-pilot.jsonl), [pilot R2 receipts](2026-11-04-f10-post-f12-pilot-r2.jsonl), and [before-F12 report](2026-11-04-f10-free-reads.md).\n")
print("Two startup attempts produced no reader cohorts: a fresh project's catalog was initialized by creating an owned database through the public API, and the optional flight-counter probe targeted an unserved metrics port. The measured run omitted that optional probe. A later external progress query overlapped fra/where_columns/N=1/repeat=2 and added 17 fair-use SQL read units, with zero billed read component. That contaminated cohort and its failure receipt are retained in startup receipts and excluded from the matrix; its repeat was measured again. The 16 preceding clean cohorts were retained on resume. The two replacement cohorts completed cleanly, then the CPU guard stopped when the GRU process disappeared during a deployment before the next cohort completed. All 18 clean cohorts were retained. Deployment identity was verified again in all three regions before resuming. A subsequent setup attempt could not find both sealed objects after eight manifest probes and stopped before N=100 readers started, with its database deleted. Another attempt was terminated at the operator’s request while an idle baseline was collecting; it completed no reader cohort. All benchmark application traffic, including cleanup, was held pending an explicit baseline OK. The operator then released a quiet baseline, requested a further hold for the F12 proof, and sent “F12 proof done.” All 36 preceding clean cohorts were archived as pilots; this report uses only the subsequent fresh matrix. The post-baseline run later stopped at fra/table/N=100/repeat=3 because live readiness had not arrived within the driver’s one-second wait. Its 50 preceding completed cohorts were retained; the failed attempt is not represented by successful-subset latencies. The retry completed after a dev-host cleanup delay, and the remaining groups resumed with the same GRU process and capacity. No external application queries were used for progress after the contaminated-cohort exclusion.\n")
proof = [row for row in rows if row.get("type") in {"cpu_capacity", "guard", "failure", "cleanup", "stop"}]
for index, row in enumerate(proof):
    print("`" + json.dumps({key: value for key, value in row.items() if key != "frames"}, separators=(",", ":")) + "`", end="\n\n" if index < len(proof) - 1 else "\n")
