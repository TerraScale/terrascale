#!/usr/bin/env python3
"""Grafana dashboards and alert rules for fly-metrics.net, as code. See README.md.

  python3 ops/grafana/grafana.py build      # rewrite dashboards/*.json and alerts.json
  python3 ops/grafana/grafana.py check      # run every query against Fly's Prometheus (flyctl login)
  GRAFANA_TOKEN_FILE=... python3 ops/grafana/grafana.py provision [--email ADDRESS]

Only the standard library: the scripts run on any dev host and in CI without installs.
"""
import json
import os
import pathlib
import re
import subprocess
import sys
import urllib.parse
import urllib.request

HERE = pathlib.Path(__file__).resolve().parent
GRAFANA = "https://fly-metrics.net"
PROMETHEUS = "https://api.fly.io/prometheus/terrascale"
FOLDER = {"uid": "terrascale", "title": "TerraScale"}
RECEIVER = "TerraScale owner"
APPS = 'app=~"ts-terrabase|ts-auth"'
# Fly's scraper adds app, region and instance to every series; "$region" is the dashboard's multi-select.
R = 'region=~"$region"'
RATE = "$__rate_interval"
MEMORY_LIMIT = 512 * 1024 * 1024


# ---------------------------------------------------------------- panels

def ts(title, targets, unit="short", desc="", stack=False, max_=None):
    """A time series panel; targets are (expr, legend) pairs."""
    defaults = {"unit": unit, "custom": {"fillOpacity": 10 if stack else 0, "stacking": {"mode": "normal" if stack else "none"}}}
    if max_ is not None:
        defaults["max"] = max_
    return {"type": "timeseries", "title": title, "description": desc, "datasource": DS,
            "fieldConfig": {"defaults": defaults, "overrides": []},
            "options": {"legend": {"displayMode": "table", "placement": "right", "calcs": ["lastNotNull", "max"]}, "tooltip": {"mode": "multi"}},
            "targets": [{"refId": chr(65 + i), "expr": e, "legendFormat": l} for i, (e, l) in enumerate(targets)]}


def stat(title, expr, legend, unit="short", thresholds=None, desc="", base="green"):
    steps = [{"color": base, "value": None}] + [{"color": c, "value": v} for v, c in (thresholds or [])]
    return {"type": "stat", "title": title, "description": desc, "datasource": DS,
            "fieldConfig": {"defaults": {"unit": unit, "thresholds": {"mode": "absolute", "steps": steps}}, "overrides": []},
            "options": {"reduceOptions": {"calcs": ["lastNotNull"]}, "colorMode": "background", "textMode": "value_and_name"},
            "targets": [{"refId": "A", "expr": expr, "legendFormat": legend, "instant": True}]}


def row(title):
    return {"type": "row", "title": title, "collapsed": False, "panels": []}


DS = {"type": "prometheus", "uid": "${datasource}"}


def traffic(app):
    f = f'app="{app}",{R}'
    count = f"http_server_request_duration_seconds_count{{{f}}}"
    bucket = f"http_server_request_duration_seconds_bucket{{{f}}}"
    return [
        row("Requests"),
        ts("Requests/s by region", [(f"sum by (region) (rate({count}[{RATE}]))", "{{region}}")], "reqps"),
        ts("Error ratio (5xx) by region", [(f'sum by (region) (rate(http_server_request_duration_seconds_count{{{f},http_response_status_code=~"5.."}}[{RATE}])) / sum by (region) (rate({count}[{RATE}]))', "{{region}}")], "percentunit"),
        ts("Latency by region (p50, p95, p99)", [(f"histogram_quantile({q}, sum by (region, le) (rate({bucket}[{RATE}])))", f"{{{{region}}}} p{int(q * 100)}") for q in (0.5, 0.95, 0.99)], "s"),
        ts("Requests/s by route (top 10)", [(f"topk(10, sum by (http_route, http_request_method) (rate({count}[{RATE}])))", "{{http_request_method}} {{http_route}}")], "reqps"),
        ts("p95 latency by route (top 10)", [(f"topk(10, histogram_quantile(0.95, sum by (http_route, le) (rate({bucket}[{RATE}]))))", "{{http_route}}")], "s"),
        ts("4xx and 5xx by route and status", [(f'sum by (http_route, http_response_status_code) (rate(http_server_request_duration_seconds_count{{{f},http_response_status_code=~"[45].."}}[{RATE}])) > 0', "{{http_response_status_code}} {{http_route}}")], "reqps"),
    ]


def runtime(app):
    f = f'app="{app}",{R}'
    return [
        row(".NET runtime"),
        ts("GC heap and working set", [(f"sum by (region) (dotnet_gc_last_collection_heap_size_bytes{{{f}}})", "{{region}} GC heap"),
                                        (f"sum by (region) (dotnet_gc_last_collection_memory_committed_size_bytes{{{f}}})", "{{region}} GC committed"),
                                        (f"sum by (region) (dotnet_process_memory_working_set_bytes{{{f}}})", "{{region}} working set")], "bytes"),
        ts("Process CPU (cores)", [(f"sum by (region) (rate(dotnet_process_cpu_time_seconds_total{{{f}}}[{RATE}]))", "{{region}}")], "short"),
        ts("Thread pool", [(f"sum by (region) (dotnet_thread_pool_thread_count{{{f}}})", "{{region}} threads"),
                           (f"sum by (region) (dotnet_thread_pool_queue_length{{{f}}})", "{{region}} queued")], "short"),
        ts("GC pause time and collections", [(f"sum by (region) (rate(dotnet_gc_pause_time_seconds_total{{{f}}}[{RATE}]))", "{{region}} pause s/s"),
                                             (f"sum by (region, gc_heap_generation) (rate(dotnet_gc_collections_total{{{f}}}[{RATE}]))", "{{region}} {{gc_heap_generation}}/s")], "short"),
    ]


def machine(app_filter):
    f = f"{app_filter},{R}"
    return [
        row("Machine (512 MB RAM + 4 GB swap, shared-cpu-2x)"),
        ts("Memory used vs the 512 MB limit", [(f"sum by (app, region) (fly_instance_memory_mem_total{{{f}}} - fly_instance_memory_mem_available{{{f}}})", "{{app}} {{region}}"),
                                               (f"vector({MEMORY_LIMIT})", "512 MB limit")], "bytes",
           "MemTotal - MemAvailable. The guest sees ~459 MB of the 512 MB."),
        ts("Swap used", [(f"sum by (app, region) (fly_instance_memory_swap_total{{{f}}} - fly_instance_memory_swap_free{{{f}}})", "{{app}} {{region}}")], "bytes",
           "Swap pages at a few MB/s (docs/fly/spike-1a.md): sustained use means the machine needs more RAM."),
        ts("CPU used (cores of 2)", [(f'sum by (app, region) (rate(fly_instance_cpu{{{f},mode!~"idle|steal"}}[{RATE}])) / 100', "{{app}} {{region}}"),
                                     (f'sum by (app, region) (rate(fly_instance_cpu{{{f},mode="steal"}}[{RATE}])) / 100', "{{app}} {{region}} steal")], "short",
           "fly_instance_cpu counts centiseconds."),
        ts("Shared CPU balance and throttling", [(f"sum by (app, region) (fly_instance_cpu_balance{{{f}}})", "{{app}} {{region}} balance"),
                                                 (f"sum by (app, region) (rate(fly_instance_cpu_throttle{{{f}}}[{RATE}]))", "{{app}} {{region}} throttled")], "short",
           "When the burst balance reaches 0, the machine is throttled to its baseline."),
        ts("Memory pressure (PSI some, avg10)", [(f'max by (app, region) (fly_instance_memory_pressure_some{{{f},pressure_duration="avg10"}})', "{{app}} {{region}}")], "percent"),
        ts("Network", [(f"sum by (app, region) (rate(fly_instance_net_recv_bytes{{{f}}}[{RATE}]))", "{{app}} {{region}} in"),
                       (f"sum by (app, region) (rate(fly_instance_net_sent_bytes{{{f}}}[{RATE}]))", "{{app}} {{region}} out")], "Bps"),
    ]


def terrabase():
    f = f'app="ts-terrabase",{R}'
    peer = "{{region}} → {{peer_region}}"
    return traffic("ts-terrabase") + [
        row("Replication (design C: docs/terrabase/multi-region-replication.md, Metrics)"),
        ts("CDC tailer lag", [(f"max by (region) (terrabase_replication_tail_lag_seconds{{{f}}})", "{{region}}")], "s",
           "Age of the oldest local commit a live peer has not acknowledged (R3)."),
        ts("CDC backlog (rows awaiting every peer's ack)", [(f"max by (region) (terrabase_replication_cdc_backlog{{{f}}})", "{{region}}")], "short",
           "turso_cdc rows kept because some peer has not acknowledged them (R3): the outbox change groups are rebuilt from."),
        ts("Send lag per peer", [(f"max by (region, peer_region) (terrabase_replication_send_lag_seconds{{{f}}})", peer)], "s",
           "Age of the oldest change group not yet sent to the peer (R4a)."),
        ts("Ack lag per peer", [(f"max by (region, peer_region) (terrabase_replication_ack_lag_seconds{{{f}}})", peer)], "s",
           "Age of the oldest change group sent but not acknowledged (R4a)."),
        ts("Convergence lag p95 (origin write → applied here)", [(f"histogram_quantile(0.95, sum by (region, peer_region, le) (rate(terrabase_replication_apply_delay_seconds_bucket{{{f}}}[{RATE}])))", "{{peer_region}} → {{region}}")], "s"),
        ts("Merge rate (groups applied/s)", [(f"sum by (region, peer_region) (rate(terrabase_replication_merged_groups_total{{{f}}}[{RATE}]))", "{{peer_region}} → {{region}}")], "short"),
        ts("Anti-entropy repairs (documents/s)", [(f"sum by (region, peer_region) (rate(terrabase_replication_repairs_total{{{f}}}[{RATE}]))", "{{peer_region}} → {{region}}")], "short"),
        ts("Clock stamps capped (skew > 1 s)", [(f"sum by (region) (increase(terrabase_clock_skew_capped_total{{{f}}}[{RATE}]))", "{{region}}")], "short"),
        ts("Changes feed holes (refill ordering broken)", [(f"sum by (region) (increase(terrabase_feed_holes_total{{{f}}}[{RATE}]))", "{{region}}")], "short"),
        row("Storage and backups"),
        ts("Data on disk and volume", [(f"max by (region) (terrabase_pressure_data_bytes{{{f}}})", "{{region}} data"),
                                       (f"max by (region) (terrabase_pressure_disk_used_bytes{{{f}}})", "{{region}} volume used"),
                                       (f"max by (region) (terrabase_pressure_disk_total_bytes{{{f}}})", "{{region}} volume size")], "bytes",
           "The volume auto-extends at 80% by 1 GB, up to 100 GB."),
        ts("Volume use", [(f"max by (region) (terrabase_pressure_disk_used_bytes{{{f}}} / terrabase_pressure_disk_total_bytes{{{f}}})", "{{region}}")], "percentunit", max_=1),
        ts("Databases", [(f"max by (region) (terrabase_stats_databases{{{f}}})", "{{region}}")], "short"),
        ts("Backup shipping lag", [(f"max by (region) (terrabase_backup_lag_seconds{{{f}}})", "{{region}}")], "s",
           "Seconds the oldest committed data not yet in Tigris has waited."),
        ts("Since the least recent shipped data", [(f"time() - min by (region) (terrabase_backup_last_success_seconds{{{f}}} > 0)", "{{region}}")], "s"),
        ts("Backup failures", [(f"sum by (region) (increase(terrabase_backup_failures_total{{{f}}}[{RATE}]))", "{{region}}")], "short"),
    ] + runtime("ts-terrabase") + machine('app="ts-terrabase"')


def auth():
    f = f'app="ts-auth",{R}'
    return traffic("ts-auth") + [
        row("Sign-ins, tokens, email"),
        ts("Sign-ins by method and outcome", [(f"sum by (method, outcome) (rate(ts_auth_sign_ins_total{{{f}}}[{RATE}]))", "{{method}} {{outcome}}")], "ops", stack=True),
        ts("Sign-in refusal ratio by region", [(f'sum by (region) (rate(ts_auth_sign_ins_total{{{f},outcome!="succeeded"}}[{RATE}])) / sum by (region) (rate(ts_auth_sign_ins_total{{{f}}}[{RATE}]))', "{{region}}")], "percentunit"),
        ts("Token responses by grant and outcome", [(f"sum by (grant_type, outcome) (rate(ts_auth_tokens_total{{{f}}}[{RATE}]))", "{{grant_type}} {{outcome}}")], "ops", stack=True),
        ts("Emails by outcome", [(f"sum by (region, outcome) (increase(ts_auth_emails_total{{{f}}}[{RATE}]))", "{{region}} {{outcome}}")], "short", stack=True),
        ts("TerraBase calls p95 and errors", [(f"histogram_quantile(0.95, sum by (region, le) (rate(http_client_request_duration_seconds_bucket{{{f}}}[{RATE}])))", "{{region}} p95"),
                                              (f'sum by (region) (rate(http_client_request_duration_seconds_count{{{f},error_type!=""}}[{RATE}]))', "{{region}} errors/s")], "s",
           "Outbound HTTP from ts-auth (TerraBase over 6PN, Cloudflare email)."),
    ] + runtime("ts-auth") + machine('app="ts-auth"')


def overview():
    count = f"http_server_request_duration_seconds_count{{{APPS},{R}}}"
    return [
        stat("Scrape up", f"up{{{APPS},{R}}}", "{{app}} {{region}}", thresholds=[(1, "green")], base="red", desc="0 = Fly's scraper can't reach /metrics (port 9091/9092, all interfaces)."),
        stat("Backup lag", f'max by (region) (terrabase_backup_lag_seconds{{app="ts-terrabase",{R}}})', "{{region}}", "s", [(300, "orange"), (600, "red")]),
        stat("Worst replication send lag", f'max by (region) (terrabase_replication_send_lag_seconds{{app="ts-terrabase",{R}}})', "{{region}}", "s", [(10, "orange"), (30, "red")]),
        row("Traffic"),
        ts("Requests/s by app and region", [(f"sum by (app, region) (rate({count}[{RATE}]))", "{{app}} {{region}}")], "reqps"),
        ts("Error ratio (5xx)", [(f'sum by (app, region) (rate(http_server_request_duration_seconds_count{{{APPS},{R},http_response_status_code=~"5.."}}[{RATE}])) / sum by (app, region) (rate({count}[{RATE}]))', "{{app}} {{region}}")], "percentunit"),
        ts("p95 latency", [(f"histogram_quantile(0.95, sum by (app, region, le) (rate(http_server_request_duration_seconds_bucket{{{APPS},{R}}}[{RATE}])))", "{{app}} {{region}}")], "s"),
        ts("Sign-ins by outcome", [(f"sum by (region, outcome) (rate(ts_auth_sign_ins_total{{{R}}}[{RATE}]))", "{{region}} {{outcome}}")], "ops", stack=True),
        row("Replication and backups"),
        ts("Replication send lag per peer", [(f'max by (region, peer_region) (terrabase_replication_send_lag_seconds{{app="ts-terrabase",{R}}})', "{{region}} → {{peer_region}}")], "s"),
        ts("Convergence lag p95", [(f'histogram_quantile(0.95, sum by (region, peer_region, le) (rate(terrabase_replication_apply_delay_seconds_bucket{{app="ts-terrabase",{R}}}[{RATE}])))', "{{peer_region}} → {{region}}")], "s"),
        ts("Backup shipping lag", [(f'max by (region) (terrabase_backup_lag_seconds{{app="ts-terrabase",{R}}})', "{{region}}")], "s"),
        ts("Volume use", [(f'max by (region) (terrabase_pressure_disk_used_bytes{{app="ts-terrabase",{R}}} / terrabase_pressure_disk_total_bytes{{app="ts-terrabase",{R}}})', "{{region}}")], "percentunit", max_=1),
    ] + machine(APPS)


def dashboard(uid, title, panels, app):
    x = y = 0
    laid = []
    for p in panels:
        w, h = (24, 1) if p["type"] == "row" else (8, 4) if p["type"] == "stat" else (12, 8)
        if x + w > 24:
            x, y = 0, y + prev_h
        p = {"id": len(laid) + 1, "gridPos": {"x": x, "y": y, "w": w, "h": h}, **p}
        laid.append(p)
        x, prev_h = x + w, h
        if p["type"] == "row":
            x, y = 0, y + 1
    region = {"type": "query", "name": "region", "label": "Region", "datasource": DS, "multi": True, "includeAll": True, "allValue": ".*",
              "current": {"text": "All", "value": "$__all"}, "refresh": 2, "sort": 1,
              "query": {"query": f"label_values(fly_instance_up{{{app}}}, region)", "refId": "region"}, "definition": f"label_values(fly_instance_up{{{app}}}, region)"}
    source = {"type": "datasource", "name": "datasource", "label": "Data source", "query": "prometheus", "current": {}}
    links = [{"type": "dashboards", "tags": ["terrascale"], "asDropdown": False, "title": "TerraScale"}]
    return {"uid": uid, "title": title, "tags": ["terrascale"], "timezone": "utc", "schemaVersion": 39, "editable": True,
            "time": {"from": "now-6h", "to": "now"}, "refresh": "1m", "links": links,
            "templating": {"list": [source, region]}, "panels": laid}


DASHBOARDS = {
    "overview": lambda: dashboard("ts-overview", "TerraScale overview", overview(), APPS),
    "terrabase": lambda: dashboard("ts-terrabase", "TerraBase", terrabase(), 'app="ts-terrabase"'),
    "ts-auth": lambda: dashboard("ts-auth", "ts-auth", auth(), 'app="ts-auth"'),
}


# ---------------------------------------------------------------- alerts

def rule(uid, title, expr, op, threshold, for_, summary, no_data="OK"):
    """A Grafana-managed rule: PromQL A, threshold C. Labels of A (app, region, peer_region) make one alert per series."""
    return {"uid": uid, "title": title, "ruleGroup": "terrascale", "folderUID": FOLDER["uid"], "condition": "C", "for": for_,
            "noDataState": no_data, "execErrState": "Error", "labels": {"team": "terrascale"},
            "annotations": {"summary": summary}, "notification_settings": {"receiver": RECEIVER},
            "data": [{"refId": "A", "relativeTimeRange": {"from": 600, "to": 0}, "datasourceUid": "${datasource}",
                      "model": {"refId": "A", "expr": expr, "instant": True}},
                     {"refId": "C", "datasourceUid": "__expr__",
                      "model": {"refId": "C", "type": "threshold", "expression": "A", "conditions": [{"evaluator": {"type": op, "params": [threshold]}}]}}]}


def alerts():
    tb = 'app="ts-terrabase"'
    return [
        rule("ts-replication-send-lag", "Replication send lag > 30 s", f"max by (region, peer_region) (terrabase_replication_send_lag_seconds{{{tb}}})", "gt", 30, "5m",
             "{{ $labels.region }} has not sent changes to {{ $labels.peer_region }} for {{ $values.A }} s."),
        rule("ts-replication-tail-lag", "CDC tailer lag > 10 s", f"max by (region) (terrabase_replication_tail_lag_seconds{{{tb}}})", "gt", 10, "5m",
             "A peer of {{ $labels.region }} has not acknowledged a local commit for {{ $values.A }} s."),
        rule("ts-feed-holes", "Changes feed hole (refill ordering broken)", f"sum by (region) (increase(terrabase_feed_holes_total{{{tb}}}[10m]))", "gt", 0, "0m",
             "{{ $labels.region }} filed a repaired event below its origin's newest event: a refill's repairs committed after that origin's later batches. Feed readers that move to another machine may miss events."),
                rule("ts-backup-lag", "Backup shipping lag > 10 min", f"max by (region) (terrabase_backup_lag_seconds{{{tb}}})", "gt", 600, "10m",
             "{{ $labels.region }} has committed data not in Tigris for {{ $values.A }} s."),
        rule("ts-error-rate", "5xx ratio > 5%",
             f'sum by (app, region) (rate(http_server_request_duration_seconds_count{{{APPS},http_response_status_code=~"5.."}}[5m]))'
             f" / (sum by (app, region) (rate(http_server_request_duration_seconds_count{{{APPS}}}[5m])) > 0.05)", "gt", 0.05, "5m",
             "{{ $labels.app }} in {{ $labels.region }} answers {{ $values.A }} of requests with 5xx (counted above 0.05 req/s)."),
        rule("ts-memory", "Memory available < 10%", f"min by (app, region) (fly_instance_memory_mem_available{{{APPS}}} / fly_instance_memory_mem_total{{{APPS}}})", "lt", 0.10, "10m",
             "{{ $labels.app }} in {{ $labels.region }} has {{ $values.A }} of RAM available; next stop is swap."),
        rule("ts-swap", "Swap used > 256 MB", f"max by (app, region) (fly_instance_memory_swap_total{{{APPS}}} - fly_instance_memory_swap_free{{{APPS}}})", "gt", 256 * 1024 * 1024, "15m",
             "{{ $labels.app }} in {{ $labels.region }} keeps {{ $values.A }} bytes in swap (it pages at a few MB/s)."),
        rule("ts-scrape-down", "Metrics scrape failing", f"min by (app, region) (up{{{APPS}}})", "lt", 1, "5m",
             "Fly can't scrape {{ $labels.app }} in {{ $labels.region }}: the machine is down or /metrics is not on all interfaces.", no_data="Alerting"),
    ]


# ---------------------------------------------------------------- commands

def build():
    out = HERE / "dashboards"
    out.mkdir(exist_ok=True)
    for name, make in DASHBOARDS.items():
        write(out / f"{name}.json", make(), "panels")
    write(HERE / "alerts.json", alerts(), None)


def write(path, value, listed):
    """One panel (or rule) per line: readable diffs, and the file stays small."""
    if listed:
        head = {k: v for k, v in value.items() if k != listed}
        lines = ",\n  ".join(json.dumps(p, ensure_ascii=False) for p in value[listed])
        body = json.dumps(head, ensure_ascii=False)[:-1] + f',\n "{listed}": [\n  {lines}\n ]}}\n'
    else:
        body = "[\n " + ",\n ".join(json.dumps(r, ensure_ascii=False) for r in value) + "\n]\n"
    json.loads(body)
    path.write_text(body)


def exprs():
    for name, make in DASHBOARDS.items():
        for p in make()["panels"]:
            for t in p.get("targets", []):
                yield f"{name}: {p['title']}", t["expr"]
    for r in alerts():
        yield f"alert: {r['title']}", r["data"][0]["model"]["expr"]


def fly_token():
    # Read-only org token, valid 1 h; never printed.
    return subprocess.run(["flyctl", "tokens", "create", "readonly", "terrascale", "-x", "1h"], check=True, capture_output=True, text=True).stdout.strip().splitlines()[-1]


def check():
    """Every query must parse and run on Fly's Prometheus; reports which ones already return data."""
    token, failed = fly_token(), 0
    for where, expr in exprs():
        q = expr.replace(R, 'region=~".*"').replace(RATE, "5m")
        req = urllib.request.Request(f"{PROMETHEUS}/api/v1/query?" + urllib.parse.urlencode({"query": q}), headers={"Authorization": token})
        try:
            body = json.load(urllib.request.urlopen(req, timeout=30))
            n = len(body["data"]["result"])
            print(f"{'data' if n else 'empty':5} {n:3}  {where}")
        except urllib.error.HTTPError as e:
            failed += 1
            print(f"ERROR      {where}: {e.code} {e.read()[:300]!r}")
    sys.exit(1 if failed else 0)


def provision(email):
    """Pushes the dashboards, the email contact point and the alert rules. Needs a Grafana service-account token (Editor or Admin)."""
    token = pathlib.Path(os.environ["GRAFANA_TOKEN_FILE"]).read_text().strip()

    def call(method, path, body=None, ok=(200, 201, 202)):
        req = urllib.request.Request(GRAFANA + path, method=method, data=None if body is None else json.dumps(body).encode(),
                                     headers={"Authorization": f"Bearer {token}", "Content-Type": "application/json", "X-Disable-Provenance": "true"})
        try:
            with urllib.request.urlopen(req, timeout=30) as resp:
                return json.loads(resp.read() or b"null")
        except urllib.error.HTTPError as e:
            if e.code in ok:
                return None
            raise SystemExit(f"{method} {path}: {e.code} {e.read()[:300]!r}")

    source = next(d["uid"] for d in call("GET", "/api/datasources") if d["type"] == "prometheus")
    if not any(f["uid"] == FOLDER["uid"] for f in call("GET", "/api/folders")):
        call("POST", "/api/folders", FOLDER)
    for name in DASHBOARDS:
        board = json.loads((HERE / "dashboards" / f"{name}.json").read_text())
        call("POST", "/api/dashboards/db", {"dashboard": board, "folderUid": FOLDER["uid"], "overwrite": True})
        print(f"dashboard {GRAFANA}/d/{board['uid']}")
    points = call("GET", "/api/v1/provisioning/contact-points")
    point = {"uid": "terrascale-owner", "name": RECEIVER, "type": "email", "settings": {"addresses": email, "singleEmail": True}}
    if any(p["uid"] == point["uid"] for p in points):
        call("PUT", f"/api/v1/provisioning/contact-points/{point['uid']}", point)
    else:
        call("POST", "/api/v1/provisioning/contact-points", point)
    rules = json.loads((HERE / "alerts.json").read_text())
    for r in rules:
        r["data"][0]["datasourceUid"] = source
    # The group is replaced whole: rules removed from alerts.json are deleted.
    call("PUT", f"/api/v1/provisioning/folder/{FOLDER['uid']}/rule-groups/terrascale",
         {"title": "terrascale", "folderUid": FOLDER["uid"], "interval": 60, "rules": rules})
    print(f"alerts {', '.join(r['title'] for r in rules)}")


if __name__ == "__main__":
    command = sys.argv[1] if len(sys.argv) > 1 else "build"
    if command == "build":
        build()
    elif command == "check":
        check()
    elif command == "provision":
        args = sys.argv[2:]
        provision(args[args.index("--email") + 1] if "--email" in args else "admin@terrascale.tech")
    else:
        raise SystemExit(__doc__)
