import json
import statistics
import subprocess
import sys
from pathlib import Path


def records(path):
    values = []
    for line in Path(path).read_text().splitlines():
        try:
            value = json.loads(line)
            if isinstance(value, dict):
                values.append(value)
        except ValueError:
            pass
    return values


rows = records(sys.argv[1])
cells = [row for row in rows if row.get("type") == "cell"]
methods = list({(row["sha"], row["host"], row["node"]): row for row in rows if row.get("type") == "method"}.values())
method = methods[0]
audit = next((row for row in rows if row.get("type") == "cpu_capacity_audit"), None)
analytics = records(sys.argv[2]) if len(sys.argv) > 2 else []
lookup = {(row["region"], row["shape"], row["readers"], row["repeat"]): row for row in analytics if row.get("type") == "r2"}


def span(values, digits=0):
    values = [value for value in values if value is not None]
    if not values:
        return "—"
    render = lambda value: f"{value:,.{digits}f}"
    center, low, high = statistics.median(values), min(values), max(values)
    return render(center) if low == high else f"{render(center)} ({render(low)}–{render(high)})"


groups = []
for region in ["gru", "sjc", "fra"]:
    for shape in ["table", "where_columns"]:
        for readers in [1, 10, 100, 1000]:
            group = sorted((row for row in cells if (row["region"], row["shape"], row["readers"]) == (region, shape, readers)), key=lambda row: row["repeat"])
            groups.append((region, shape, readers, group))

matrix_complete = len(cells) == 72 and all(len(group) == 3 and [r["repeat"] for r in group] == [1, 2, 3] for _, _, _, group in groups)
delivered = sum(row["snapshot"]["count"] == row["readers"] and row["tail"]["count"] == row["readers"] for row in cells)
r2_count = sum(row.get("status") == "available" and row.get("get_object") is not None for row in lookup.values())
complete = matrix_complete and delivered == 72 and r2_count == 72 and audit is None and not any(row.get("type") in ["failure", "guard"] for row in rows)
print("# F10: live SQL shape reads through the JS SDK — 2026-11-04\n")
print(f"Measured matrix: {'complete' if matrix_complete else 'incomplete'} ({len(cells)}/72 cohorts). Full reader delivery: {delivered}/72. Available R2 receipts: {r2_count}/72. Performance qualification: {'complete' if complete else 'incomplete'}.\n")
if audit:
    factor = audit["reported_processors"] / audit["quota_cores"]
    equivalent = max(row["cpu_max_percent"] * factor for row in cells if row["cpu_max_percent"] is not None)
    print(f"**Stopped; CPU safety qualification failed.** The guard divided CPU time by {audit['reported_processors']:g} runtime-reported processors. The later allocation audit found a {audit['quota_cores']:g}-core cgroup quota with {audit['affinity_cpus']} CPUs in the affinity mask. The largest recorded sample is equivalent to {equivalent:.1f}% at that quota. All reader load was stopped at `{audit['stopped_at']}` and was not restarted. Historical cgroup quotas were not logged, so these results cannot prove compliance with the 70% limit. The guard is corrected; its new capacity calculation was checked with telemetry only.\n")
    over = sorted((row for row in cells if row["cpu_max_percent"] is not None and row["cpu_max_percent"] * factor > 70), key=lambda row: row["start"])
    if over:
        first = over[0]
        print(f"The first recorded window above the two-core equivalent threshold was `{first['start']}`–`{first['end']}` ({first['region']}, {first['shape']}, N={first['readers']}, repeat {first['repeat']}). The incorrect denominator let subsequent cohorts continue until the explicit stop. Those records remain visible and carry no safety qualification.\n")
    peak = max((row for row in cells if row["cpu_max_percent"] is not None), key=lambda row: row["cpu_max_percent"])
    origin = sum(value for key, value in peak["requests"].items() if key != "cdn")
    print("## Peak CPU cohort\n")
    print("| Egress | Shape | N | Repeat | CPU max % (2 cgroup CPUs; conditional) | CDN HIT / GET attempts | CDN HIT % | Total origin requests |")
    print("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |")
    print(f"| {peak['region']} | {peak['shape']} | {peak['readers']} | {peak['repeat']} | {peak['cpu_max_percent'] * factor:.1f} | {peak['statuses'].get('HIT', 0)} / {peak['requests']['cdn']} | {peak['cdn_hit_ratio'] * 100:.2f} | {origin:,} |\n")
    print(f"This is the whole cohort window `{peak['start']}`–`{peak['end']}`, not request counts for the exact CPU sampling interval. The maximum came from {peak['cpu_samples']} CPU samples. Correcting the denominator gives `{peak['cpu_max_percent']:.8f}% × {audit['reported_processors']:g} / {audit['quota_cores']:g} = {peak['cpu_max_percent'] * factor:.8f}%` of the two-core quota. Raw cohort records retain the original four-processor value; the corrected basis is primary in the tables. Historical quota uncertainty still applies.\n")
    print("| Origin request category | Attempts in peak cohort |")
    print("| --- | ---: |")
    for key, label in [("bootstrap", "Bootstrap"), ("manifest", "Manifest"), ("keys", "Epoch keys"), ("head", "Head probes"), ("tail", "Live tail"), ("snapshot_payload", "Snapshot payload fallback"), ("catchup_payload", "Catch-up payload fallback")]:
        print(f"| {label} | {peak['requests'][key]:,} |")
    print(f"\nOf {origin:,} origin attempts, {peak['origin_statuses'].get('429', 0):,} returned HTTP 429; these refusals are already included in the category counts. There were {peak['statuses'].get('MISS', 0)} CDN MISS responses. Sealed catch-up payloads had no origin fallback in this cohort. Bootstrap, manifest, key and live-tail/head work still reached origin despite the high CDN HIT ratio. This establishes a remaining origin fan-out gap; shared-service CPU and cohort totals do not attribute the CPU peak to a particular endpoint or prove that CPU scales with N.\n")
    print("Recommended next task: prioritize F12 to bound live-tail resource use and coalesce upstream tail/head work per authorized shape, with authorization-scoped sharing of repeated bootstrap/manifest/key work. Preserve tenant/filter isolation, authorization and revocation. Tail-only coalescing leaves the bootstrap/manifest/key amplification visible above. Longer catch-up CDN TTLs are not the first fix supported by this cohort: its catch-up payload fallback count was zero and there were only two CDN misses. Keep reader load stopped pending the parent's decision and implementation qualification.\n")
print("## Findings\n")
populated = [group for _, _, _, group in groups if group]
print(f"- {len(cells)} recorded cohorts delivered {sum(row['tail']['count'] for row in cells):,} reader updates. {sum(len(group) == 3 for group in populated)}/{len(populated)} populated groups have three repeats; missing groups and the 1,000-reader pilot remain unqualified.")
if cells and all(row["sql_read_units"] == 0 and row["billed_read_units_after"] == 0 for row in cells):
    print("- Every recorded reader window had zero SQL read units and zero billed read component. Setup and the live update still consumed write units.")
available = [row["get_object"] for row in lookup.values() if row.get("status") == "available" and row.get("get_object") is not None]
if available:
    print(f"- R2 bucket analytics reported {span(available)} GetObject requests per recorded window ({len(available)} windows). These are time-window bucket counts, not exact per-object attribution.")
print("- Total origin requests grow with N. Independent subscriptions still bootstrap, fetch manifests and keys, probe the head and poll the tail; 429 responses add retries and some immutable-offset fallback requests. A CDN HIT ratio near 99% does not establish flat total origin cost.")
print("- Regional latency comparisons include shared dev-host scheduling, the SSH tunnel and SDK control requests. They do not establish native regional browser or isolated CDN latency.\n")
print("## Method\n")
print("- F6 protocol/setup references: `edd4f9c8c` and `3baf6b501`. These identify the CDN contract and prior live proof; the current serving binary was not independently pinned by this run.")
print("- Measured checkouts: " + ", ".join(f"`{item['sha']}` ({item['host']}, Node {item['node']})" for item in methods) + "; shipped CDN reader: `37d7e4bb99a7db2178ede8d3e530de6e17c3e558`.")
print("- Generators are admitted with `ts-heavy-any`; no load generator runs on serving hosts. Completed cohorts are retained across resumed runs without being measured again.")
print("- One throwaway Free account and one active gru home database per run, with a fresh table and cold object URLs for every cohort. Each table has 16 rows and a 128-byte text field; the filtered shape uses `active = 1` and columns `id,version` (8 rows).")
print("- Public origin reads publish one snapshot and a one-row catch-up update before measurement; both sealed objects must be listed in the manifest. These setup requests are excluded. The measured readers use `db.shape().subscribe()` without a replacement transport or synthesized response.")
print("- Each region runs separately through a temporary loopback-only SSH CONNECT egress proxy in gru, sjc or fra. Reported Cloudflare locations come from `CF-Ray`. Latencies include dev-host/tunnel transit and SDK control requests; they are not native regional browser latency or a CDN-only latency estimate.")
print("- N independent subscriptions start together, reach the snapshot and current head, then receive one committed row update. Snapshot latency ends at the first materialized snapshot callback; tail latency starts before the write request and ends at each updated callback. The writer stays on the dev host.")
print("- A cohort that hits its deadline is recorded and the next cohort proceeds, unless the CPU guard aborts the run. Percentiles require every reader in that phase; a completed subset never substitutes for the full cohort. The tail update is not attempted if the full cohort cannot reach the current head.")
print(f"- Target: three measured repeats for every shape/region/N; the repeats column records completed cohorts. Nearest-rank p50/p99 within each repeat; tables show the median and min–max of completed repeat percentiles; fewer than three repeats remain unqualified. N=1 supplies one latency sample per repeat, so its p99 is not a statistically useful tail estimate. Cohort deadline: {method.get('cohort_deadline_seconds', 60)} seconds.")
print("- The authenticated bootstrap, manifests, keys, empty head probes and live tail remain origin requests. Counts include retries and refusals, exclude setup, usage observations and OAuth exchanges, and are separated from immutable payload requests. A shared token provider refreshes the synthetic account's 60-second access token before expiry.")
if audit:
    print("- These cohorts sampled TerraBase runtime CPU-time counters once per second and normalized them using the runtime processor count. This denominator was wrong for the allocated service quota. The primary CPU column uses the observed two-core cgroup quota: original percentage × 4 / 2. The invalid four-processor values remain alongside it for auditability. The correction is conditional on that quota holding during the historical windows and does not retroactively qualify the guard. The corrected guard identifies the `terrabase` process, reads `/proc` CPU ticks and uses the minimum of CPU affinity and all ancestor cgroup v2 quotas; it stops on missing data, process/capacity changes or utilization above 70%. CPU includes other concurrent TerraBase work, with no idle-baseline subtraction.")
else:
    print("- CPU is sampled every second from the identified TerraBase process and normalized against the minimum of affinity and ancestor cgroup v2 quotas. Every phase stops above 70%, or if the process, capacity or telemetry changes. Table values are the highest sampled one-second mean, not instantaneous CPU, and include other concurrent TerraBase work without idle-baseline subtraction.")
print("- Usage observations include the database's best-effort operation counter and the project's monthly ledger. SQL read-unit deltas are fair-use units; the billed read component is `month.units - month.writeUnits`. This is ledger evidence, not a Stripe invoice or an infrastructure-dollar estimate.")
print("- R2 counts are Cloudflare `r2OperationsAdaptiveGroups` results for `ts-terrabase-shapes-prod`, rounded outward to whole-second reader windows. They are bucket-wide adaptive analytics, may include unrelated traffic/adjacent setup, and are not per-object attribution. An empty analytics result is unavailable, not zero. Client MISS counts are not substituted for R2 GetObject counts.\n")
print("- This is a small two-object workload. It does not establish latency or origin cost for larger snapshots, longer catch-up histories or browser bootstrap behavior.\n")
print("## Observed CDN locations\n")
print("| Regional egress | CF-Ray location | Responses |")
print("| --- | --- | ---: |")
for region in ["gru", "sjc", "fra"]:
    colos = {}
    for row in cells:
        if row["region"] == region:
            for colo, count in row["colos"].items():
                colos[colo] = colos.get(colo, 0) + count
    for colo, count in sorted(colos.items()):
        print(f"| {region} | {colo} | {count:,} |")
    if not colos:
        print(f"| {region} | — | — |")
print()
print("## Latency (milliseconds)\n")
print("| Egress | Shape | N | Complete / measured repeats | Snapshot p50 | Snapshot p99 | Catch-up p50 | Catch-up p99 | Tail p50 | Tail p99 |")
print("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |")
for region, shape, readers, group in groups:
    values = ["unavailable" if any(row[phase]["count"] != readers for row in group) else span([row[phase][metric] for row in group], 1) for phase in ["snapshot", "catchup", "tail"] for metric in ["p50_ms", "p99_ms"]]
    full = sum(row["snapshot"]["count"] == readers and row["tail"]["count"] == readers for row in group)
    print(f"| {region} | {shape} | {readers} | {full}/{len(group)} | " + " | ".join(values) + " |")
print("\n## Requests, read units and CPU\n")
cpu_headers = "CPU max % (2 cgroup CPUs; conditional) | CPU max % (original 4-CPU basis; invalid)" if audit else "CPU max % (allocated capacity) | CPU max % (original basis)"
print("| Egress | Shape | N | CDN GET attempts | CDN HIT % | Origin immutable requests | Origin control/tail requests | Total origin requests | Origin requests / reader | R2 GetObject (r1/r2/r3) | " + cpu_headers + " | SQL read units | Billed read component |")
print("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | ---: | ---: | ---: | ---: |")
for region, shape, readers, group in groups:
    r2 = [lookup.get((region, shape, readers, repeat), {}).get("get_object") for repeat in [1, 2, 3]]
    counts = [span([row["requests"]["cdn"] for row in group]), span([100 * row["cdn_hit_ratio"] for row in group], 2),
              span([row["requests"]["snapshot_payload"] + row["requests"]["catchup_payload"] for row in group]),
              span([sum(row["requests"][key] for key in ["bootstrap", "manifest", "keys", "head", "tail"]) for row in group]),
              span([sum(value for key, value in row["requests"].items() if key != "cdn") for row in group]),
              span([sum(value for key, value in row["requests"].items() if key != "cdn") / readers for row in group], 2),
              "/".join("—" if count is None else str(count) for count in r2),
              span([max((row["cpu_max_percent"] * (audit["reported_processors"] / audit["quota_cores"] if audit else 1) for row in group if row["cpu_max_percent"] is not None), default=None)], 1),
              span([max((row["cpu_max_percent"] for row in group if row["cpu_max_percent"] is not None), default=None)], 1) if audit else "—",
              span([row["sql_read_units"] for row in group]), span([row["billed_read_units_after"] for row in group])]
    print(f"| {region} | {shape} | {readers} | " + " | ".join(counts) + " |")
timeouts = [row for row in cells if row.get("outcome") == "timeout"]
if timeouts:
    print("\n## Cohorts that reached the deadline\n")
    print("| Egress | Shape | N | Repeat | Snapshot readers | Current-head readers | Updated readers |")
    print("| --- | --- | ---: | ---: | ---: | ---: | ---: |")
    for row in timeouts:
        print(f"| {row['region']} | {row['shape']} | {row['readers']} | {row['repeat']} | {row['snapshot']['count']} | {row['catchup']['count']} | {row['tail']['count']} |")
print("\n## Evidence\n")
print("[Cohort records](2026-11-04-f10-free-reads.jsonl), [R2 query records](2026-11-04-f10-free-reads-r2.jsonl) and [stop/pilot history](2026-11-04-f10-free-reads-stops.jsonl) accompany this report. Setup/pilot runs are excluded from measured summaries. A 1,000-reader gru/table pilot hit the 180-second deadline before the driver retained censored-cohort receipts; it is disclosed in the stop history and is not counted as three measured repeats. Earlier setup, scope, tunnel-readiness and telemetry stops are also retained.\n")
print("The [corrected-guard validation](2026-11-04-f10-free-reads-cpu-validation.jsonl) contains five telemetry-only samples with a two-core quota and four CPUs of affinity. No reader load was restarted for this validation.\n")
print("| Measured checkout | Source | Measured Git blob |")
print("| --- | --- | --- |")
for item in {item["sha"]: item for item in methods}.values():
    for path in ["benchmark/scripts/free-reads.mjs", "benchmark/scripts/free-reads-cpu.py", "benchmark/scripts/free-reads-egress.py", "sdk/js/src/v1/table-shape.js", "sdk/js/src/v1/shape-cdn.js", "sdk/js/src/v1/transport.js", "sdk/js/src/v1/usage.js"]:
        blob = subprocess.check_output(["git", "rev-parse", f"{item['sha']}:{path}"], text=True).strip()
        print(f"| `{item['sha'][:10]}` | `{path}` | `{blob}` |")
for row in rows:
    if row.get("type") in ["guard", "failure", "cleanup", "stop", "cpu_capacity_audit"]:
        print("\n`" + json.dumps({key: value for key, value in row.items() if key != "frames"}, separators=(",", ":")) + "`")
