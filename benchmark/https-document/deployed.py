#!/usr/bin/env python3
"""Benchmark the deployed gru-1 TerraBase from a real client over the public internet.

The load runs on a homelab client (`--client beta`, or `local`) against the public
regional host through Cloudflare, exactly like a customer. A dedicated database is
created through ts-cli (the public dashboard API) for each run and deleted afterwards.
Server-side member metrics are read over NetBird SSH while the load runs.
"""
import argparse
import contextlib
import datetime
import json
import os
from pathlib import Path
import re
import secrets
import shlex
import signal
import ssl
import subprocess
import tempfile
import threading
import time
import urllib.error
import urllib.request

import acceptance

ROOT = acceptance.ROOT
API = "https://gru.api.terrascale.tech"
# The public endpoints whose errors abort a run (dashboard and ts-auth).
GUARDS = {
    "api": API + "/healthz",
    "auth": "https://gru.auth.terrascale.tech/terrascale/.well-known/openid-configuration",
    "dashboard": "https://dash.terrascale.tech/health/ready",
}
LB_ID = "ocid1.loadbalancer.oc1.sa-saopaulo-1.aaaaaaaawziu3epvzdahcls7kxcxxetcur5jrjjzunjeokagnfd3nhwh2gra"
# TS-578's final `terrabase` set: ctl-1..3 only. Until then, only smoke runs.
FINAL_LB = {"10.251.0.11:9465", "10.251.0.12:9465", "10.251.0.13:9465"}
MEMBER_BACKENDS = {
    "gru-1-ctl-1": "10.251.0.11:9465", "gru-1-ctl-2": "10.251.0.12:9465", "gru-1-ctl-3": "10.251.0.13:9465",
    "gru-1-db-1": "10.251.0.21:9465", "gru-1-db-2": "10.251.0.22:9465",
}
MEMBERS = tuple(MEMBER_BACKENDS)
VAULT = "ocid1.vault.oc1.sa-saopaulo-1.ffvmfu2naagt6.abtxeljr7ebplea4ua3memgthsupcfn3crg6d56vipsw7o2butchdrx3urmq"
# OCI A1 list price per member (1 OCPU at $0.01/h, 6 GB at $0.0015/GB-h), above the free tier.
MEMBER_USD_PER_HOUR = 0.01 + 6 * 0.0015
WORKLOADS = ["point_read", "point_write", "update", "delete", "query_filter_sort", "list_page", "transactions", "mixed_80_20"]
MODES = {
    "smoke": {"concurrency": [1, 4, 16], "operations_per_worker": 10, "max_ops_per_second": 50, "abort_p99_ms": 3000, "timeout_s": 1200},
    "full": {"concurrency": [1, 2, 4, 8, 16, 32, 64], "operations_per_worker": 100, "max_ops_per_second": 400, "abort_p99_ms": 2000, "timeout_s": 5400},
}
PAYLOADS = {"small": 0, "large": 16384}
# Reads only: process CPU, working set, GC and the process's disk I/O (SQLite and its journal).
SAMPLER = r"""pid=$(pgrep -xo terrabase) || exit 3
end=$(($(date +%s) + {limit}))
while [ "$(date +%s)" -lt "$end" ] && kill -0 "$pid"; do
  echo "T $(date +%s.%N)"
  curl -s --max-time 2 127.0.0.1:9464/metrics | awk '!/^#/ && /^dotnet_(process_cpu_time_seconds_total|process_memory_working_set_bytes|gc_collections_total|gc_pause_time_seconds_total|gc_heap_total_allocated_bytes_total)/ {{sub(/\{{.*/, "", $1); print $1, $2}}'
  awk '/^(read_bytes|write_bytes):/ {{sub(":", "", $1); print "io_" $1, $2}}' /proc/$pid/io
  sleep 5
done"""


# Cloudflare refuses Python's default user agent; identify the benchmark instead.
USER_AGENT = "ts-benchmark/1 (TS-606)"


def get(url, timeout=10):
    try:
        request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
        with urllib.request.build_opener(urllib.request.ProxyHandler({})).open(request, timeout=timeout) as response:
            return response.status
    except urllib.error.HTTPError as error:
        return error.code
    except OSError:
        return None


OCI_TIMEOUT_S = 120
NETBIRD_TIMEOUT_S = 30


def lb_members():
    out = subprocess.run(["oci", "lb", "backend", "list", "--region", "sa-saopaulo-1", "--load-balancer-id", LB_ID, "--backend-set-name", "terrabase"], check=True, capture_output=True, text=True, timeout=OCI_TIMEOUT_S).stdout
    return {item["name"]: {"offline": item["offline"], "drain": item["drain"]} for item in json.loads(out)["data"]}


def active(members):
    return {name for name, state in members.items() if not state["offline"] and not state["drain"]}


# An unreachable client fails within about 30 s instead of blocking database cleanup.
SSH_OPTIONS = ["-o", "BatchMode=yes", "-o", "ConnectTimeout=10", "-o", "ServerAliveInterval=10", "-o", "ServerAliveCountMax=3"]
CLIENT_TIMEOUT_S = 120


def client_argv(client, command):
    # The client's login shell may not be POSIX (beta runs fish).
    bash = ["bash", "-c", command]
    return bash if client == "local" else ["ssh", *SSH_OPTIONS, client, shlex.join(bash)]


def client_shell(client, command):
    argv = client_argv(client, command)
    return subprocess.run(argv, check=True, capture_output=True, text=True, timeout=CLIENT_TIMEOUT_S).stdout


# Labeled lines: a field a platform lacks (ARM has no "model name") cannot shift the others.
CLIENT_PROBE = r"""echo "host=$(hostname)"; echo "cpus=$(nproc)"; echo "model=$(grep -m1 'model name' /proc/cpuinfo | cut -d: -f2)"
echo "loadavg=$(cat /proc/loadavg)"; echo "stat=$(head -1 /proc/stat)"
for i in 1 2 3 4 5 6 7 8 9 10; do curl -s -o /dev/null -w 'rtt=%{time_connect} %{time_appconnect} %{time_total} %{http_code}\n' --max-time 10 -A 'ts-benchmark/1 (TS-606)' https://gru.api.terrascale.tech/healthz; done"""


@contextlib.contextmanager
def client_scratch(client, remote, files):
    """Copies `files` into a private directory on a remote client and always removes it, token first."""
    if client == "local":
        yield
        return
    try:
        client_shell(client, f"install -d -m 700 {remote}")
        subprocess.run(["scp", "-q", *SSH_OPTIONS, *files, f"{client}:{remote}/"], check=True, timeout=600)
        yield
    finally:
        client_shell(client, f"shred -u {remote}/token 2>/dev/null; rm -rf {remote}")


def client_state(client):
    fields, probes = {}, []
    for line in client_shell(client, CLIENT_PROBE).splitlines():
        key, _, value = line.partition("=")
        if key == "rtt":
            probes.append(value)
        else:
            fields[key] = value
    rtts = sorted([float(value) * 1000 for value in probe.split()[:3]] for probe in probes if probe.endswith(" 200"))
    if not rtts:
        raise RuntimeError("the client got no 200 from gru.api /healthz")
    # Each duration's own median: the columns need not sort the same way.
    median = [sorted(column)[len(column) // 2] for column in zip(*rtts)]
    return {
        "host": fields["host"], "cpu_count": int(fields["cpus"]), "cpu_model": fields.get("model", "").strip() or "unknown", "loadavg": fields["loadavg"],
        "cpu_jiffies": jiffies(fields["stat"]),
        "rtt_ms": {"tcp_connect": median[0], "tls_ready": median[1], "healthz_total": median[2], "samples": len(rtts)},
    }


def jiffies(stat_line):
    """The counters of a `/proc/stat` `cpu` line."""
    return [int(value) for value in stat_line.split()[1:]]


def client_cpu_busy(before, after):
    # Fields 1..8 end at steal; guest and guest_nice are already counted in user and nice.
    delta = [b - a for a, b in zip(before["cpu_jiffies"][:8], after["cpu_jiffies"][:8])]
    idle = delta[3] + delta[4]
    return round(100 * (1 - idle / sum(delta)), 1) if sum(delta) else None


def ts(cli, *args):
    """Run ts-cli with the operator's dashboard token; only JSON output is parsed, never printed."""
    out = subprocess.run([cli, *args, "--json"], check=True, capture_output=True, text=True, timeout=TS_CLI_TIMEOUT_S).stdout
    return json.loads(out)


TS_CLI_TIMEOUT_S = 120
STOP_GRACE_S = 10


def stop(process, grace=None):
    """Terminate, then kill after a grace period; never waits unbounded on a stuck process."""
    if process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=STOP_GRACE_S if grace is None else grace)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()


def find(value, predicate):
    """First nested string value satisfying `predicate` (CLI output shapes vary by command)."""
    if isinstance(value, dict):
        value = list(value.values())
    if isinstance(value, list):
        return next((found for item in value if (found := find(item, predicate)) is not None), None)
    return value if isinstance(value, str) and predicate(value) else None


class CreateFailed(RuntimeError):
    """A create whose outcome is unknown; `resource_id` is what a lookup by name found, if any."""

    def __init__(self, kind, error, resource_id):
        super().__init__(f"{kind} create failed ({type(error).__name__}); found by name: {resource_id is not None}")
        self.resource_id = resource_id


def find_named(value, name):
    """The `id` of the first object named `name` in CLI list output."""
    if isinstance(value, dict):
        if value.get("name") == name and isinstance(value.get("id"), str):
            return value["id"]
        value = list(value.values())
    if isinstance(value, list):
        return next((found for item in value if (found := find_named(item, name)) is not None), None)
    return None


def create(cli, kind, name, *args):
    """Creates `kind` named `name`. A timeout, unreadable reply or Ctrl-C may hide a create that
    the server did, so the run-unique name is looked up and handed to cleanup as `resource_id`:
    on CreateFailed, or on the re-raised KeyboardInterrupt, which keeps the cancellation."""
    try:
        return ts(cli, kind, "create", "--name", name, *args)
    except (subprocess.CalledProcessError, subprocess.TimeoutExpired, ValueError, KeyboardInterrupt) as error:
        try:
            resource_id = find_named(ts(cli, kind, "list"), name)
        except (subprocess.CalledProcessError, subprocess.TimeoutExpired, ValueError):
            resource_id = None
        if isinstance(error, KeyboardInterrupt):
            error.resource_id = resource_id
            raise
        raise CreateFailed(kind, error, resource_id) from None


def create_tracked(cli, kind, name, attempted, id_pattern, *args):
    """`create()`, registering `kind` in `attempted` first: from then on cleanup deletes the resource,
    looking it up by name whenever no ID was recognized, also for a successful create whose
    output has an unexpected shape. Returns the output and the recognized ID, or None."""
    attempted.add(kind)
    output = create(cli, kind, name, *args)
    resource_id = find_named(output, name) or find(output, lambda value: re.fullmatch(id_pattern, value) is not None)
    return output, resource_id


def netbird_ips(netbird):
    # Runs after the database and key exist: a hung NetBird client must fail into cleanup, not block it.
    out = subprocess.run(shlex.split(netbird) + ["status", "-d"], check=True, capture_output=True, text=True, timeout=NETBIRD_TIMEOUT_S).stdout
    return dict(re.findall(r"^ (gru-1-[a-z]+-\d)\.nb\.ts\.internal:\n\s+NetBird IP: (\S+)", out, re.M))


def start_samplers(work, netbird, limit_s, samplers):
    """Starts one sampler per member, registering each in the caller's `samplers` as it starts."""
    ips = netbird_ips(netbird)
    for member in MEMBERS:
        if member not in ips:
            continue
        command = [os.environ.get("TS_ORCH", "ts-orch"), "netbird", "ssh", "--vault-id", VAULT, "--netbird", netbird, "--", f"root@{ips[member]}", SAMPLER.format(limit=limit_s)]
        with (work / f"member-{member}.log").open("w") as log:
            samplers.append((member, subprocess.Popen(command, stdout=log, stderr=subprocess.DEVNULL)))


MEMBER_METRICS = {
    "dotnet_process_cpu_time_seconds_total", "dotnet_process_memory_working_set_bytes", "dotnet_gc_collections_total",
    "dotnet_gc_pause_time_seconds_total", "dotnet_gc_heap_total_allocated_bytes_total", "io_read_bytes", "io_write_bytes",
}


def missing_members(member_stats, lb_active, lb_checked):
    """Serving members without metrics: every LB-active backend (all members when the LB was not
    read) needs a summary, and an active backend this runner does not know is missing too."""
    known = {backend: member for member, backend in MEMBER_BACKENDS.items()}
    required = [known.get(backend, backend) for backend in lb_active] if lb_checked else list(MEMBERS)
    return sorted(member for member in required if not member_stats.get(member))


def member_summary(text, start, end):
    """CPU %, peak RSS, GC and disk I/O between `start` and `end` from one sampler log."""
    ticks, current = [], None
    for line in text.splitlines():
        key, _, value = line.partition(" ")
        if key == "T":
            current = {"t": float(value)}
            ticks.append(current)
        elif current is not None and value:
            current[key] = current.get(key, 0) + float(value)
    # A tick whose metrics scrape or /proc read failed is incomplete; missing is not zero.
    ticks = [tick for tick in ticks if start - 6 <= tick["t"] <= end + 6 and MEMBER_METRICS <= tick.keys()]
    if len(ticks) < 2:
        return None
    first, last = ticks[0], ticks[-1]
    delta = {key: last.get(key, 0) - first.get(key, 0) for key in last}
    return {
        "seconds": round(delta["t"], 1),
        "cpu_percent": round(100 * delta["dotnet_process_cpu_time_seconds_total"] / delta["t"], 1),
        "rss_peak_mb": round(max(tick["dotnet_process_memory_working_set_bytes"] for tick in ticks) / 2**20, 1),
        "gc_collections": int(delta["dotnet_gc_collections_total"]),
        "gc_pause_ms": round(1000 * delta["dotnet_gc_pause_time_seconds_total"], 1),
        "allocated_mb": round(delta["dotnet_gc_heap_total_allocated_bytes_total"] / 2**20, 1),
        "disk_read_mb": round(delta["io_read_bytes"] / 2**20, 2),
        "disk_write_mb": round(delta["io_write_bytes"] / 2**20, 2),
        "complete_ticks": len(ticks),
    }


class Guard(threading.Thread):
    """Polls the public guards every 2 s; two consecutive failures of one guard abort the run."""

    def __init__(self, abort):
        super().__init__(daemon=True)
        self.abort, self.stop, self.log, self.tripped = abort, threading.Event(), [], None

    def run(self):
        failures = dict.fromkeys(GUARDS, 0)
        while not self.stop.wait(2):
            for name, url in GUARDS.items():
                status = get(url, timeout=5)
                failures[name] = 0 if status == 200 else failures[name] + 1
                if status != 200:
                    self.log.append({"t": time.time(), "guard": name, "status": status})
                if failures[name] >= 2 and self.tripped is None:
                    self.tripped = f"{name} answered {status} twice"
                    self.abort()


PAYLOAD_VERDICT = ("status", "driver_status", "aborted", "driver_exit_code", "evidence_copied")


def payload_result(receipt, returncode, copied, tripped):
    """The driver's receipt with the runner's verdict: a guard abort, nonzero exit or failed copy
    overrides the driver's own `passed`."""
    result = {"aborted": None, **receipt, "driver_status": receipt.get("status"), "driver_exit_code": returncode, "evidence_copied": copied}
    if tripped:
        result.update(status="aborted", aborted=tripped)
    elif returncode != 0 or not copied or result.get("status") is None:
        result["status"] = "failed" if result.get("status") in ("passed", None) else result["status"]
    return result


HEARTBEAT_S = 2
LEASE_S = 10


def watchdog(command, timeout_s, stat_prefix=None):
    """Runs `command` on the client under its own guards, in its own process group.

    The client enforces the duration bound itself (`timeout`, 30 s after the runner's), and the
    controller's stdin heartbeat is a lease: once it stops for LEASE_S, or stdin closes because
    the runner stopped or lost its connection, the whole group gets SIGTERM, then SIGKILL.
    """
    # Client CPU counters right around the driver, so setup and transfers are not averaged in.
    stat = (lambda suffix: f"head -1 /proc/stat > {shlex.quote(stat_prefix + suffix)}") if stat_prefix else (lambda _suffix: ":")
    return f"""set -m
exec 3<&0
{stat(".before")}
timeout --kill-after={STOP_GRACE_S}s {int(timeout_s) + 30}s {command} 3<&- &
pid=$!
{{ while read -r -t {LEASE_S} _ <&3; do :; done; kill -TERM -- -$pid; sleep {STOP_GRACE_S}; kill -KILL -- -$pid; }} >/dev/null 2>&1 &
watcher=$!
killer=
# The wrapper exits only once the driver's group is gone (zombies count: a container's PID 1 may
# never reap them, so the wait is bounded).
finish() {{
  kill -KILL -- -$pid 2>/dev/null
  for _ in 1 2 3 4 5 6 7 8 9 10; do kill -0 -- -$pid 2>/dev/null || break; sleep 0.1; done
  kill -- -$watcher 2>/dev/null
  [ -n "$killer" ] && kill -- -$killer 2>/dev/null
}}
trap 'kill -TERM -- -$pid 2>/dev/null; {{ sleep {STOP_GRACE_S}; kill -KILL -- -$pid; }} >/dev/null 2>&1 & killer=$!; wait $pid; finish; exit 143' TERM
wait $pid; rc=$?
{stat(".after")}
finish
exit $rc"""


def driver_command(driver, remote, payload):
    """The driver invocation as one shell-safe string (TMPDIR may contain spaces)."""
    return shlex.join([driver, "--config", f"{remote}/{payload}.json", "--token-file", f"{remote}/token", "--output", f"{remote}/{payload}"])


def run_payload(client, command, timeout_s, remote, stat_prefix=None):
    """Runs one driver under the health guard and the duration bound.

    The driver and the guard are always stopped, also on Ctrl-C, before the caller reads the
    guard's state or deletes the database: the driver holds the key in memory.
    """
    process = subprocess.Popen(client_argv(client, watchdog(command, timeout_s, stat_prefix)), stdin=subprocess.PIPE)
    beating = threading.Event()

    def heartbeat():
        while not beating.wait(HEARTBEAT_S):
            try:
                process.stdin.write(b"\n")
                process.stdin.flush()
            except (OSError, ValueError):
                return

    heart = threading.Thread(target=heartbeat, daemon=True)
    heart.start()
    guard = Guard(process.terminate)
    guard.start()
    deadline = time.monotonic() + timeout_s
    timed_out = False
    try:
        # A guard trip ends the wait at once; stop() below escalates to SIGKILL after its grace.
        while process.poll() is None and guard.tripped is None and time.monotonic() < deadline:
            try:
                process.wait(timeout=1)
            except subprocess.TimeoutExpired:
                pass
        # Decided before cleanup, whose own time must not turn a finished run into a timeout.
        timed_out = process.poll() is None and guard.tripped is None
    finally:
        guard.stop.set()
        guard.join()
        # Ending the lease stops the driver on the client even when no later SSH gets through.
        beating.set()
        heart.join()
        with contextlib.suppress(OSError):
            process.stdin.close()
        # The watchdog needs up to one grace to TERM, then KILL, the driver's group.
        stop(process, grace=2 * STOP_GRACE_S + 5)
        if client != "local":
            # The bracket keeps the pattern from matching this cleanup shell itself.
            pattern = shlex.quote(f"[{remote[0]}]{remote[1:]}/")
            client_shell(client, f"pkill -f {pattern}; sleep {STOP_GRACE_S}; pkill -KILL -f {pattern} || true")
    if timed_out and guard.tripped is None:
        guard.tripped = "duration bound reached"
    return process.returncode, guard


def seed(opener, auth, db_path, rate):
    """Creates and seeds one collection per payload (driver document IDs repeat per payload),
    at most `rate` requests per second, stopping when the health guard trips."""
    requests = ((db_path + "/collections", "POST", {"name": payload}) if rank is None else
                (f"{db_path}/collections/{payload}/documents/{acceptance.document_id(rank)}", "PUT", seed_document(rank, size))
                for payload, size in PAYLOADS.items() for rank in (None, *range(32)))
    guard = Guard(lambda: None)
    guard.start()
    try:
        for path, method, body in requests:
            if guard.tripped:
                raise RuntimeError(f"setup aborted: {guard.tripped}")
            started = time.monotonic()
            acceptance.request(opener, API, path, method, body, auth, (201,))
            time.sleep(max(0.0, 1 / rate - (time.monotonic() - started)))
    finally:
        guard.stop.set()
        guard.join()


def seed_document(rank, payload_bytes):
    """Must equal the driver's `document()`: large runs read and query padded documents."""
    document = {"rank": rank, "group": rank % 2, "payload": "ts500"}
    return {**document, "padding": "x" * payload_bytes} if payload_bytes else document


def cost_per_million(ops_s, members):
    """None without throughput or without a checked, nonempty LB member set."""
    return round(members * MEMBER_USD_PER_HOUR / (ops_s * 3600) * 1e6, 4) if ops_s and members else None


def scoreboard(output, meta, runs, members):
    lines = [f"# Deployed TerraBase benchmark, gru-1 ({meta['mode']})", "",
             f"Run `{output.name}` · {meta['started']} · status **{meta['status']}**", "",
             "| Client | CPUs | Load before → after | Client CPU busy | RTT connect / TLS / healthz ms | LB `terrabase` active |",
             "| --- | ---: | --- | ---: | --- | --- |"]
    client, rtt = meta["client"], meta["client"]["rtt_ms"]
    lines.append(f"| {client['host']} ({client['cpu_model']}) | {client['cpu_count']} | {client['loadavg'].split()[0]} → {meta['client_after']['loadavg'].split()[0]} | {'—' if meta['client_cpu_busy_percent'] is None else str(meta['client_cpu_busy_percent']) + '%'} | {rtt['tcp_connect']:.1f} / {rtt['tls_ready']:.1f} / {rtt['healthz_total']:.1f} | {', '.join(meta['lb_active']) if meta['lb_checked'] else 'unchecked'} |")
    lines += ["", "| Payload | Workload | Concurrency | ops/s | Errors | p50 ms | p95 ms | p99 ms | max ms | US$ per M ops |", "| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |"]
    for payload, receipt in runs.items():
        for row in receipt.get("rows", []):
            ms = [acceptance_ms(row.get(f"latency_{name}_ns")) for name in ("p50", "p95", "p99", "max")]
            lines.append(f"| {payload} | {row['workload']} | {row['concurrency']} | {row['throughput_ops_s']:.1f} | {row['errors']} | {' | '.join(ms)} | {cost_per_million(row['throughput_ops_s'], len(meta['lb_active'])) or '—'} |")
        if receipt.get("aborted"):
            lines.append(f"\n**{payload} aborted:** {receipt['aborted']}\n")
    if meta.get("member_coverage_missing"):
        lines += ["", f"**Member metrics missing for serving members:** {', '.join(meta['member_coverage_missing'])}."]
    lines += ["", "| Member | Seconds | CPU % | Peak RSS MB | GC collections | GC pause ms | Allocated MB | Disk read MB | Disk write MB |", "| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |"]
    for member, summary in members.items():
        cells = " | ".join(str(summary[key]) for key in ("seconds", "cpu_percent", "rss_peak_mb", "gc_collections", "gc_pause_ms", "allocated_mb", "disk_read_mb", "disk_write_mb")) if summary else "no samples | | | | | | | "
        lines.append(f"| {member} | {cells} |")
    lines += ["", "Client-side closed-loop latency through Cloudflare, rate-bounded; a smoke run never saturates the cell, so its US$ per million ops is an upper bound "
              f"(LB members × US${MEMBER_USD_PER_HOUR:.3f}/h ÷ ops/s). Member CPU is process CPU time over wall time on 1 OCPU. "
              "[Run receipt](receipt.json) · per-payload `*/receipt.json` and `samples.jsonl`.", ""]
    (output / "summary.md").write_text("\n".join(lines))


RECONCILE_TRIES = 4
RECONCILE_DELAY_S = 15


class Unresolved(Exception):
    """A create whose outcome stayed unknown: no resource with the run's name ever appeared."""


def resolve(cli, kind, name):
    """Looks a resource up by the run-unique name, retrying: a timed-out create can commit later."""
    for attempt in range(RECONCILE_TRIES):
        if attempt:
            time.sleep(RECONCILE_DELAY_S)
        try:
            found = find_named(ts(cli, kind, "list"), name)
        except (subprocess.CalledProcessError, subprocess.TimeoutExpired, ValueError):
            continue
        if found is not None:
            return found
    raise Unresolved(f"no {kind} named {name} after {RECONCILE_TRIES} lookups")


def cleanup(samplers, cli, record, database, name, key_id, attempted=()):
    """Stop samplers, delete the database and revoke the key; every step runs even if another fails.

    A database or key in `attempted` without an ID (a create that failed ambiguously or answered
    in an unexpected shape) is looked up by name, with retries, and an outcome that stays unknown
    is reported, never silently skipped.
    """
    def delete_database():
        target = database or resolve(cli, "databases", name)
        ts(cli, "databases", "delete", "--database-id", target, "--confirm-name", name, "--yes")
        record.update(id=target, deleted=True)

    def revoke_key():
        target = key_id or resolve(cli, "keys", name)
        ts(cli, "keys", "revoke", "--key-id", target, "--yes")
        record.update(key_id=target, key_revoked=True)

    steps = [(f"sampler {member}", lambda process=process: stop(process)) for member, process in samplers]
    if database or "databases" in attempted:
        steps.append(("delete database", delete_database))
    if key_id or "keys" in attempted:
        steps.append(("revoke key", revoke_key))
    errors = []
    for step, action in steps:
        try:
            action()
        except Unresolved as error:
            errors.append(f"{step}: unresolved, {error}")
        except (OSError, ValueError, subprocess.CalledProcessError, subprocess.TimeoutExpired) as error:
            errors.append(f"{step}: {type(error).__name__}")
    return errors


def acceptance_ms(ns):
    return "—" if ns is None else f"{ns / 1e6:.1f}"


def run(args):
    mode = MODES["full" if args.full else "smoke"]
    started = datetime.datetime.now(datetime.timezone.utc)
    meta = {"schema_version": 1, "mode": "full" if args.full else "smoke", "started": started.isoformat(timespec="seconds"), "status": "failed", **acceptance.benchmark_source_state()}
    output = args.output
    output.mkdir(parents=True)
    try:
        members = lb_members() if args.lb_check else {}
        meta["lb"], meta["lb_checked"], meta["lb_active"] = members, args.lb_check, sorted(active(members))
        if args.full and active(members) != FINAL_LB:
            raise RuntimeError("full runs wait for TS-578's final LB set (ctl-1..3 only)")
        for name, url in GUARDS.items():
            if get(url) != 200:
                raise RuntimeError(f"{name} is unhealthy before the run")
        meta["client"] = client_state(args.client)
        cli, project = os.environ.get("TS_CLI", "ts"), os.environ.get("TERRASCALE_PROJECT_ID")
        if not project or not os.environ.get("TERRASCALE_DASHBOARD_TOKEN"):
            raise RuntimeError("TERRASCALE_PROJECT_ID and TERRASCALE_DASHBOARD_TOKEN must name the dedicated benchmark project")
        name = "bench-" + output.name[-12:].lower().replace("_", "-")
        database = key_id = None
        attempted = set()  # kinds created or maybe created; cleanup looks up any without an ID
        samplers = []
        try:
            try:
                _, database = create_tracked(cli, "databases", name, attempted, r"[0-9a-f-]{36}", "--region", "GRU")
            except (CreateFailed, KeyboardInterrupt) as error:
                database = getattr(error, "resource_id", None)
                meta["database"] = {"name": name, "id": database, "reconciled": True}
                raise
            meta["database"] = {"name": name, "id": database}
            if database is None:
                raise RuntimeError("database create returned no recognizable ID; cleanup looks it up by name")
            try:
                key, key_id = create_tracked(cli, "keys", name, attempted, r"tsk_\S+", "--scopes", "documents:read,documents:write", "--database-id", database)
            except (CreateFailed, KeyboardInterrupt) as error:
                key_id = getattr(error, "resource_id", None)
                meta["database"]["key_id"] = key_id
                raise
            meta["database"]["key_id"] = key_id
            secret = find(key, lambda value: value.startswith("tb_"))
            if key_id is None or secret is None:
                raise RuntimeError("key create returned no recognizable ID or secret; cleanup revokes it by name")
            auth = {"Authorization": "Bearer " + secret, "User-Agent": USER_AGENT}
            opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), urllib.request.HTTPSHandler(context=ssl.create_default_context()))
            db_path = f"/v1/projects/{project}/databases/{name}"
            seed(opener, auth, db_path, mode["max_ops_per_second"])
            if args.metrics:
                start_samplers(output, args.netbird, mode["timeout_s"] * len(PAYLOADS) + 120, samplers)
                time.sleep(15)
            runs, load_start, cpu_delta = {}, time.time(), None
            with tempfile.TemporaryDirectory(prefix="ts606-") as scratch:
                local = Path(scratch)
                remote = f"/tmp/ts606-{secrets.token_hex(8)}" if args.client != "local" else scratch
                (local / "token").write_text(secret)
                (local / "token").chmod(0o600)
                for payload, size in PAYLOADS.items():
                    config = {"endpoint_override": API, "atlas_url": "https://atlas.terrascale.tech", "project": project, "database": name, "collection": payload, "server_version": "deployed gru-1", "concurrency": mode["concurrency"], "operations_per_worker": mode["operations_per_worker"], "timeout_ms": 10000, "workloads": WORKLOADS, "payload_bytes": size, "max_ops_per_second": mode["max_ops_per_second"], "abort_p99_ms": mode["abort_p99_ms"]}
                    acceptance.write_json(local / f"{payload}.json", config)
                driver = args.driver if args.client == "local" else f"{remote}/{Path(args.driver).name}"
                with client_scratch(args.client, remote, [args.driver, *(str(local / f) for f in ("token", "small.json", "large.json"))]):
                    for payload in PAYLOADS:
                        command = driver_command(driver, remote, payload)
                        stat_prefix = f"{remote}/{payload}-cpu"
                        returncode, guard = run_payload(args.client, command, mode["timeout_s"], remote, stat_prefix)
                        cpu = client_shell(args.client, f"cat {shlex.quote(stat_prefix + '.before')} {shlex.quote(stat_prefix + '.after')} 2>/dev/null || true").splitlines()
                        if len(cpu) == 2:
                            busy = [b - a for a, b in zip(*(jiffies(line) for line in cpu))]
                            cpu_delta = [total + value for total, value in zip(cpu_delta or [0] * len(busy), busy)]
                        meta.setdefault("guard_failures", []).extend(guard.log)
                        # Keep whatever was copied, but a partial copy fails the payload.
                        if args.client != "local":
                            copy = ["scp", "-q", "-r", *SSH_OPTIONS, f"{args.client}:{remote}/{payload}", str(output)]
                        else:
                            copy = ["cp", "-r", f"{remote}/{payload}", str(output)]
                        try:
                            copied = subprocess.run(copy, check=False, timeout=600).returncode == 0
                        except subprocess.TimeoutExpired:
                            copied = False
                        receipt_path = output / payload / "receipt.json"
                        runs[payload] = payload_result(json.loads(receipt_path.read_text()) if receipt_path.exists() else {}, returncode, copied, guard.tripped)
                        # The runner's verdict is authoritative; the copied driver receipt stays as the driver wrote it.
                        meta.setdefault("payloads", {})[payload] = {key: runs[payload].get(key) for key in PAYLOAD_VERDICT}
                        if runs[payload].get("status") != "passed":
                            break
            load_end = time.time()
            meta["client_after"] = client_state(args.client)
            # Only the driver intervals; None when no payload recorded both snapshots.
            meta["client_cpu_busy_percent"] = client_cpu_busy({"cpu_jiffies": [0] * len(cpu_delta)}, {"cpu_jiffies": cpu_delta}) if cpu_delta else None
        finally:
            cleanup_errors = cleanup(samplers, cli, meta.get("database", {}), database, name, key_id, attempted)
            if cleanup_errors:
                meta["cleanup_errors"] = cleanup_errors
        sampled = {member for member, _ in samplers}
        # Every member is listed; one that was not sampled (or had no complete samples) shows as unavailable.
        member_stats = {member: member_summary((output / f"member-{member}.log").read_text(), load_start, load_end) if member in sampled else None for member in MEMBERS}
        meta["members"] = member_stats
        if args.metrics:
            meta["member_coverage_missing"] = missing_members(member_stats, meta["lb_active"], meta["lb_checked"])
        meta["status"] = "passed" if len(runs) == len(PAYLOADS) and all(receipt.get("status") == "passed" for receipt in runs.values()) and "cleanup_errors" not in meta and not meta.get("member_coverage_missing") else "aborted"
        scoreboard(output, meta, runs, member_stats)
    finally:
        acceptance.write_json(output / "receipt.json", meta)
    if meta["status"] != "passed":
        raise RuntimeError(f"run {meta['status']}; see {output / 'receipt.json'}")


def terminate(signum, _frame):
    """SIGTERM unwinds like Ctrl-C, through every `finally` that deletes the database and key;
    later signals are ignored so they cannot interrupt that cleanup."""
    signal.signal(signal.SIGTERM, signal.SIG_IGN)
    raise KeyboardInterrupt(f"signal {signum}")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output-parent", type=Path, default=ROOT / "bench/deployed" / datetime.date.today().isoformat())
    parser.add_argument("--client", default="beta", help="ssh host that runs the load, or 'local'")
    parser.add_argument("--driver", help="driver executable; default: build it and use Cargo's reported artifact")
    parser.add_argument("--full", action="store_true", help="full ramp; refused until TS-578's LB set is final")
    parser.add_argument("--no-lb-check", dest="lb_check", action="store_false", help="CI smoke without OCI credentials")
    parser.add_argument("--no-metrics", dest="metrics", action="store_false", help="skip member metrics (no NetBird)")
    parser.add_argument("--netbird", default="sudo -n netbird-wt0")
    args = parser.parse_args(argv)
    args.output = args.output_parent / ("run-" + datetime.datetime.now(datetime.timezone.utc).strftime("%H%M%SZ") + "-" + secrets.token_hex(3))
    print(f"output: {args.output}", flush=True)
    signal.signal(signal.SIGTERM, terminate)
    try:
        args.driver = args.driver or acceptance.build_driver()
        run(args)
    except (OSError, RuntimeError, subprocess.CalledProcessError, subprocess.TimeoutExpired, ValueError) as error:
        # CLI and API errors may echo responses; print only the error type and our own messages.
        parser.exit(1, f"deployed: {error if isinstance(error, RuntimeError) else type(error).__name__}\n")


if __name__ == "__main__":
    main()
