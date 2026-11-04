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
complete = matrix_complete and delivered == 72 and r2_count == 72 and not any(row.get("type") in ["failure", "guard"] for row in rows)
print("# F10: live SQL shape reads through the JS SDK — 2026-11-04\n")
print(f"Measured matrix: {'complete' if matrix_complete else 'incomplete'} ({len(cells)}/72 cohorts). Full reader delivery: {delivered}/72. Available R2 receipts: {r2_count}/72. Performance qualification: {'complete' if complete else 'incomplete'}.\n")
print("## Method\n")
print("- Measured checkouts: " + ", ".join(f"`{item['sha']}` ({item['host']}, Node {item['node']})" for item in methods) + "; shipped CDN reader: `37d7e4bb99a7db2178ede8d3e530de6e17c3e558`.")
print("- Generators are admitted with `ts-heavy-any`; no load generator runs on serving hosts. Completed cohorts are retained across resumed runs without being measured again.")
print("- One throwaway Free account, one gru home database, fresh table and cold object URLs for every cohort. Each table has 16 rows and a 128-byte text field; the filtered shape uses `active = 1` and columns `id,version` (8 rows).")
print("- Public origin reads publish one snapshot and a one-row catch-up update before measurement; both sealed objects must be listed in the manifest. These setup requests are excluded. The measured readers use `db.shape().subscribe()` without a replacement transport or synthesized response.")
print("- Each region runs separately through a temporary loopback-only SSH CONNECT egress proxy in gru, sjc or fra. Reported Cloudflare locations come from `CF-Ray`. Latencies include dev-host/tunnel transit and SDK control requests; they are not native regional browser latency or a CDN-only latency estimate.")
print("- N independent subscriptions start together, reach the snapshot and current head, then receive one committed row update. Snapshot latency ends at the first materialized snapshot callback; tail latency starts before the write request and ends at each updated callback. The writer stays on the dev host.")
print("- A cohort that hits its deadline is recorded and the next cohort proceeds, unless the CPU guard aborts the run. Percentiles require every reader in that phase; a completed subset never substitutes for the full cohort. The tail update is not attempted if the full cohort cannot reach the current head.")
print(f"- Target: three measured repeats for every shape/region/N; the repeats column records completed cohorts. Nearest-rank p50/p99 within each repeat; tables show the median and min–max of completed repeat percentiles; fewer than three repeats remain unqualified. N=1 supplies one latency sample per repeat, so its p99 is not a statistically useful tail estimate. Cohort deadline: {method.get('cohort_deadline_seconds', 60)} seconds.")
print("- The authenticated bootstrap, manifests, keys, empty head probes and live tail remain origin requests. Counts include retries and refusals, exclude setup, usage observations and OAuth exchanges, and are separated from immutable payload requests. A shared token provider refreshes the synthetic account's 60-second access token before expiry.")
print("- CPU is sampled every second from TerraBase's runtime CPU-time counters in gru, divided by its reported processor count. Every phase stops if the sample exceeds 70%, telemetry disappears or the process restarts. Table values are the highest sampled one-second mean, not instantaneous CPU. They include other concurrent TerraBase work; no idle-baseline subtraction isolates the benchmark's CPU cost.")
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
print("| Egress | Shape | N | CDN GET attempts | CDN HIT % | Origin immutable requests | Origin control/tail requests | Total origin requests | Origin requests / reader | R2 GetObject (r1/r2/r3) | CPU max % | SQL read units | Billed read component |")
print("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | ---: | ---: | ---: |")
for region, shape, readers, group in groups:
    r2 = [lookup.get((region, shape, readers, repeat), {}).get("get_object") for repeat in [1, 2, 3]]
    counts = [span([row["requests"]["cdn"] for row in group]), span([100 * row["cdn_hit_ratio"] for row in group], 2),
              span([row["requests"]["snapshot_payload"] + row["requests"]["catchup_payload"] for row in group]),
              span([sum(row["requests"][key] for key in ["bootstrap", "manifest", "keys", "head", "tail"]) for row in group]),
              span([sum(value for key, value in row["requests"].items() if key != "cdn") for row in group]),
              span([sum(value for key, value in row["requests"].items() if key != "cdn") / readers for row in group], 2),
              "/".join("—" if count is None else str(count) for count in r2),
              span([max((row["cpu_max_percent"] for row in group if row["cpu_max_percent"] is not None), default=None)], 1),
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
print("Raw cohort records and R2 query records accompany this report. Setup/pilot runs are excluded from measured summaries. All account, database and egress-proxy cleanup must be confirmed separately before declaring F10 done.\n")
print("| Measured checkout | Source | Measured Git blob |")
print("| --- | --- | --- |")
for item in {item["sha"]: item for item in methods}.values():
    for path in ["benchmark/scripts/free-reads.mjs", "benchmark/scripts/free-reads-cpu.py", "benchmark/scripts/free-reads-egress.py", "sdk/js/src/v1/table-shape.js", "sdk/js/src/v1/shape-cdn.js", "sdk/js/src/v1/transport.js", "sdk/js/src/v1/usage.js"]:
        blob = subprocess.check_output(["git", "rev-parse", f"{item['sha']}:{path}"], text=True).strip()
        print(f"| `{item['sha'][:10]}` | `{path}` | `{blob}` |")
for row in rows:
    if row.get("type") in ["guard", "failure", "cleanup"]:
        print("\n`" + json.dumps({key: value for key, value in row.items() if key != "frames"}, separators=(",", ":")) + "`")
