#!/usr/bin/env bash
# E-16 tenant-scale qualification (development/qualification only).
#
# This command has two deliberately separate lanes:
#
#   tenant-scale.sh validate evidence.json
#       Cheap, hermetic evidence validation.  This is the only lane suitable
#       for a portable test.
#
#   TERRABASE_E16_ALLOW_EXPENSIVE=1 tenant-scale.sh qualify ...
#       Expensive live-member run.  It creates exactly 100,000 databases via
#       the member's public API, measures a real victim under neighbour load,
#       samples the member's process while idle, and writes evidence which is
#       then passed through the validator above.
#
# The expensive lane refuses to run without a member PID and a cgroup quota.
# It never treats an in-memory fixture, stub, placeholder, or skipped create
# as a qualification result.
set -euo pipefail

usage() {
  cat >&2 <<'USAGE'
usage:
  scripts/tenant-scale.sh validate <evidence.json>
  TERRABASE_E16_ALLOW_EXPENSIVE=1 scripts/tenant-scale.sh qualify \
    --api-url URL --metrics-url URL --api-key-file FILE --member-pid PID [options]

qualify options:
  --api-url URL              Real member public API base (required)
  --metrics-url URL          Real member Prometheus endpoint (required)
  --api-key KEY              API key (prefer --api-key-file)
  --api-key-file FILE        File containing the API key
  --member-pid PID           PID of the member being qualified (required)
  --out FILE                 Evidence output (default: e16-tenant-scale.json)
  --create-workers N         Concurrent create workers (default: 64)
  --victim-samples N         Solo and loaded victim samples (default: 1000)
  --neighbour-samples N      Adversarial neighbour samples (default: 10000)
  --idle-seconds N           Idle observation window (default: 30)
USAGE
  exit 2
}

fail() {
  printf 'tenant-scale: ERROR: %s\n' "$*" >&2
  exit 1
}

validate() {
  local evidence=${1:-}
  [[ -n "$evidence" && $# -eq 1 ]] || usage
  [[ -f "$evidence" ]] || fail "evidence file does not exist: $evidence"
  python3 - "$evidence" <<'PY'
import json
import ipaddress
import math
import pathlib
import sys
import urllib.parse

path = pathlib.Path(sys.argv[1])
try:
    document = json.loads(path.read_text(encoding="utf-8"))
except (OSError, json.JSONDecodeError) as error:
    print(f"tenant-scale: ERROR: evidence is not valid JSON: {error}", file=sys.stderr)
    raise SystemExit(1)

errors = []

def get(container, key, where):
    if not isinstance(container, dict) or key not in container:
        errors.append(f"missing {where}.{key}")
        return None
    return container[key]

def number(value, where, *, positive=False):
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
        errors.append(f"{where} must be a finite number")
        return None
    if positive and value <= 0:
        errors.append(f"{where} must be greater than zero")
    return float(value)

def integer(value, where, *, minimum=None):
    if isinstance(value, bool) or not isinstance(value, int):
        errors.append(f"{where} must be an integer")
        return None
    if minimum is not None and value < minimum:
        errors.append(f"{where} must be at least {minimum}")
    return value

def text(value, where):
    if not isinstance(value, str) or not value.strip():
        errors.append(f"{where} must be non-empty text")
        return None
    return value

def url(value, where, *, require_metrics_path=False):
    value = text(value, where)
    if value is None:
        return None
    parsed = urllib.parse.urlsplit(value)
    if parsed.scheme not in {"http", "https"} or not parsed.hostname or parsed.username or parsed.password:
        errors.append(f"{where} must be an HTTP(S) URL without credentials")
        return None
    if parsed.query or parsed.fragment:
        errors.append(f"{where} must not contain a query or fragment")
    if require_metrics_path and parsed.path.rstrip("/") != "/internal/metrics":
        errors.append(f"{where} must end in /internal/metrics")
    # A qualification receipt cannot point at an arbitrary public collector.
    # Production metrics are private member endpoints; permit loopback,
    # RFC-1918/RFC-4193 addresses, and explicitly private hostnames only.
    try:
        address = ipaddress.ip_address(parsed.hostname)
    except ValueError:
        hostname = parsed.hostname.lower().rstrip(".")
        if not (hostname.endswith((".internal", ".local")) or "." not in hostname):
            errors.append(f"{where} must identify a private member endpoint")
    else:
        if not (address.is_private or address.is_loopback):
            errors.append(f"{where} must identify a private member endpoint")
    return parsed

def flag(value, where):
    if not isinstance(value, bool):
        errors.append(f"{where} must be boolean")
        return None
    return value

# A string from a fake/stub/placeholder harness is never evidence.  The
# recursive scan also catches someone hiding a skipped-work marker in metadata.
for stack in [document]:
    todo = [([], stack)]
    while todo:
        keys, value = todo.pop()
        if isinstance(value, dict):
            todo.extend((keys + [str(k)], child) for k, child in value.items())
        elif isinstance(value, list):
            todo.extend((keys + [str(i)], child) for i, child in enumerate(value))
        elif isinstance(value, str):
            lowered = value.strip().lower()
            if lowered in {"todo", "placeholder", "synthetic", "mock", "stub", "fake", "skipped", "skip", "n/a"}:
                errors.append(f"placeholder or substitute marker at {'.'.join(keys)}")

if not isinstance(document, dict):
    errors.append("evidence root must be an object")
else:
    if get(document, "schema", "root") != "terrabase.e16.tenant-scale/v1":
        errors.append("root.schema is not terrabase.e16.tenant-scale/v1")
    if get(document, "qualification", "root") != "E-16":
        errors.append("root.qualification is not E-16")
    if get(document, "status", "root") != "passed":
        errors.append("root.status must be passed")
    if get(document, "mode", "root") != "live-member":
        errors.append("root.mode must be live-member")
    if get(document, "transport", "root") != "member-http":
        errors.append("root.transport must be member-http")

    subject = get(document, "subject", "root")
    if isinstance(subject, dict):
        count = integer(get(subject, "database_count", "subject"), "subject.database_count", minimum=100000)
        created = integer(get(subject, "created_count", "subject"), "subject.created_count", minimum=0)
        verified = integer(get(subject, "verified_count", "subject"), "subject.verified_count", minimum=100000)
        skipped = integer(get(subject, "skipped", "subject"), "subject.skipped", minimum=0)
        placeholders = integer(get(subject, "placeholder_databases", "subject"), "subject.placeholder_databases", minimum=0)
        if count is not None and count != 100000:
            errors.append("subject.database_count must be exactly 100000")
        if count is not None and created is not None and created != count:
            errors.append("subject.created_count must equal subject.database_count")
        if verified is not None and verified != count:
            errors.append("subject.verified_count must equal subject.database_count")
        if skipped is not None and skipped != 0:
            errors.append("subject.skipped must be zero")
        if placeholders is not None and placeholders != 0:
            errors.append("subject.placeholder_databases must be zero")
        if get(subject, "storage", "subject") != "sqlite":
            errors.append("subject.storage must be sqlite")
        if flag(get(subject, "synthetic", "subject"), "subject.synthetic") is not False:
            errors.append("subject.synthetic must be false")
        if flag(get(subject, "member_api", "subject"), "subject.member_api") is not True:
            errors.append("subject.member_api must be true")
    else:
        errors.append("missing subject object")

    envelope = get(document, "envelope", "root")
    if isinstance(envelope, dict):
        cpu = number(get(envelope, "cpu_ocpu", "envelope"), "envelope.cpu_ocpu")
        memory = number(get(envelope, "memory_gib", "envelope"), "envelope.memory_gib")
        if cpu is not None and abs(cpu - 2.0) > 1e-9:
            errors.append("envelope.cpu_ocpu must be exactly 2")
        if memory is not None and abs(memory - 12.0) > 1e-9:
            errors.append("envelope.memory_gib must be exactly 12")
        if flag(get(envelope, "cgroup_verified", "envelope"), "envelope.cgroup_verified") is not True:
            errors.append("envelope.cgroup_verified must be true")
    else:
        errors.append("missing envelope object")

    metrics = get(document, "metrics", "root")
    if isinstance(metrics, dict):
        if get(metrics, "source", "metrics") != "member-prometheus":
            errors.append("metrics.source must be member-prometheus")
        url(get(metrics, "endpoint", "metrics"), "metrics.endpoint", require_metrics_path=True)
        scrapes = integer(get(metrics, "scrapes", "metrics"), "metrics.scrapes", minimum=2)
        required = get(metrics, "required", "metrics")
        before = get(metrics, "observed_before", "metrics")
        observed = get(metrics, "observed", "metrics")
        missing = get(metrics, "missing", "metrics")
        if not isinstance(required, list) or not required:
            errors.append("metrics.required must be a non-empty list")
            required = []
        if not isinstance(observed, dict):
            errors.append("metrics.observed must be a sample-count object")
            observed = {}
        if not isinstance(before, dict):
            errors.append("metrics.observed_before must be a sample-count object")
            before = {}
        if not isinstance(missing, list):
            errors.append("metrics.missing must be a list")
            missing = []
        if missing:
            errors.append("metrics.missing must be empty")
        for metric in required:
            if not isinstance(metric, str) or not metric:
                errors.append("metrics.required contains a non-name")
            else:
                integer(before.get(metric), f"metrics.observed_before.{metric}", minimum=1)
                integer(observed.get(metric), f"metrics.observed.{metric}", minimum=1)
    else:
        errors.append("missing metrics object")

    paths = get(document, "api_paths", "root")
    if isinstance(paths, dict):
        exact = {
            "health": "/api/v1/health",
            "regions": "/api/v1/regions",
            "create_database": "/api/v1/databases",
        }
        for name, expected in exact.items():
            if get(paths, name, "api_paths") != expected:
                errors.append(f"api_paths.{name} must be {expected}")
        for name in ("get_database", "victim_probe", "neighbour_probe"):
            value = text(get(paths, name, "api_paths"), f"api_paths.{name}")
            if value is not None and (not value.startswith("/api/v1/databases/") or "{" in value or "}" in value):
                errors.append(f"api_paths.{name} must be a concrete member database path")
    else:
        errors.append("missing api_paths object")

    run = get(document, "run", "root")
    if isinstance(run, dict):
        if flag(get(run, "executed", "run"), "run.executed") is not True:
            errors.append("run.executed must be true")
        if integer(get(run, "errors", "run"), "run.errors", minimum=0) != 0:
            errors.append("run.errors must be zero")
        if text(get(run, "member_pid", "run"), "run.member_pid") is None:
            pass
    else:
        errors.append("missing run object")

    create = get(document, "create", "root")
    if isinstance(create, dict):
        requests = integer(get(create, "requests", "create"), "create.requests", minimum=0)
        successes = integer(get(create, "successes", "create"), "create.successes", minimum=0)
        p99 = number(get(create, "p99_seconds", "create"), "create.p99_seconds", positive=True)
        if requests != 100000:
            errors.append("create.requests must be exactly 100000")
        if successes != 100000:
            errors.append("create.successes must be exactly 100000")
        if p99 is not None and p99 >= 5.0:
            errors.append("create.p99_seconds must be less than 5")
    else:
        errors.append("missing create object")

    idle = get(document, "idle", "root")
    if isinstance(idle, dict):
        seconds = number(get(idle, "seconds", "idle"), "idle.seconds", positive=True)
        cpu_seconds = number(get(idle, "cpu_seconds", "idle"), "idle.cpu_seconds")
        ratio = number(get(idle, "cpu_ratio", "idle"), "idle.cpu_ratio")
        if seconds is not None and seconds < 30:
            errors.append("idle.seconds must be at least 30")
        if cpu_seconds is not None and cpu_seconds < 0:
            errors.append("idle.cpu_seconds cannot be negative")
        if ratio is not None and ratio > 0.01:
            errors.append("idle.cpu_ratio must be at most 0.01 (approximately zero)")
        if seconds and cpu_seconds is not None and abs(ratio - cpu_seconds / seconds) > 0.00001:
            errors.append("idle.cpu_ratio does not match cpu_seconds / seconds")
        if flag(get(idle, "approximately_zero", "idle"), "idle.approximately_zero") is not True:
            errors.append("idle.approximately_zero must be true")
    else:
        errors.append("missing idle object")

    victim = get(document, "victim", "root")
    if isinstance(victim, dict):
        solo = number(get(victim, "solo_p99_seconds", "victim"), "victim.solo_p99_seconds", positive=True)
        loaded = number(get(victim, "under_neighbour_p99_seconds", "victim"), "victim.under_neighbour_p99_seconds", positive=True)
        ratio = number(get(victim, "ratio", "victim"), "victim.ratio", positive=True)
        solo_requests = integer(get(victim, "solo_requests", "victim"), "victim.solo_requests", minimum=1000)
        loaded_requests = integer(get(victim, "loaded_victim_requests", "victim"), "victim.loaded_victim_requests", minimum=1000)
        neighbour_requests = integer(get(victim, "neighbour_requests", "victim"), "victim.neighbour_requests", minimum=10000)
        if solo is not None and loaded is not None and ratio is not None:
            if abs(ratio - loaded / solo) > 0.00001:
                errors.append("victim.ratio does not match loaded_p99 / solo_p99")
            if ratio > 2.0:
                errors.append("victim ratio must be at most 2")
        if flag(get(victim, "adversarial_neighbour", "victim"), "victim.adversarial_neighbour") is not True:
            errors.append("victim.adversarial_neighbour must be true")
    else:
        errors.append("missing victim object")

if errors:
    print("tenant-scale: FAIL", file=sys.stderr)
    for error in errors:
        print(f"  - {error}", file=sys.stderr)
    raise SystemExit(1)
print(f"tenant-scale: valid E-16 evidence: {path}")
PY
}

qualify() {
  [[ "${TERRABASE_E16_ALLOW_EXPENSIVE:-0}" == "1" ]] || fail "qualification creates 100,000 databases; set TERRABASE_E16_ALLOW_EXPENSIVE=1"
  command -v python3 >/dev/null 2>&1 || fail "python3 is required"
  command -v curl >/dev/null 2>&1 || fail "curl is required"

  local api_url="" metrics_url="" api_key="" api_key_file="" member_pid="" out="e16-tenant-scale.json"
  local create_workers=64 victim_samples=1000 neighbour_samples=10000 idle_seconds=30
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --api-url) [[ $# -ge 2 ]] || usage; api_url=$2; shift 2 ;;
      --metrics-url) [[ $# -ge 2 ]] || usage; metrics_url=$2; shift 2 ;;
      --api-key) [[ $# -ge 2 ]] || usage; api_key=$2; shift 2 ;;
      --api-key-file) [[ $# -ge 2 ]] || usage; api_key_file=$2; shift 2 ;;
      --member-pid) [[ $# -ge 2 ]] || usage; member_pid=$2; shift 2 ;;
      --out) [[ $# -ge 2 ]] || usage; out=$2; shift 2 ;;
      --create-workers) [[ $# -ge 2 ]] || usage; create_workers=$2; shift 2 ;;
      --victim-samples) [[ $# -ge 2 ]] || usage; victim_samples=$2; shift 2 ;;
      --neighbour-samples) [[ $# -ge 2 ]] || usage; neighbour_samples=$2; shift 2 ;;
      --idle-seconds) [[ $# -ge 2 ]] || usage; idle_seconds=$2; shift 2 ;;
      *) usage ;;
    esac
  done
  [[ -n "$api_url" && -n "$metrics_url" && -n "$member_pid" ]] || usage
  [[ "$member_pid" =~ ^[0-9]+$ && -r "/proc/$member_pid/stat" ]] || fail "--member-pid must identify a readable Linux member process"
  if [[ -n "$api_key_file" ]]; then
    [[ -r "$api_key_file" ]] || fail "API key file is not readable: $api_key_file"
    api_key=$(tr -d ' \t\r\n' <"$api_key_file")
  fi
  [[ -n "$api_key" ]] || fail "provide --api-key or --api-key-file"
  [[ "$create_workers" =~ ^[1-9][0-9]*$ && "$victim_samples" =~ ^[1-9][0-9]*$ && "$neighbour_samples" =~ ^[1-9][0-9]*$ && "$idle_seconds" =~ ^[0-9]+$ ]] || fail "worker, sample, and idle values are invalid"
  (( idle_seconds >= 30 )) || fail "idle observation must be at least 30 seconds"

  local out_dir
  out_dir=$(dirname -- "$out")
  mkdir -p -- "$out_dir"
  python3 - "$api_url" "$metrics_url" "$api_key" "$member_pid" "$out" "$create_workers" "$victim_samples" "$neighbour_samples" "$idle_seconds" <<'PY'
import concurrent.futures
import ipaddress
import json
import math
import os
import pathlib
import ssl
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

api_url, metrics_url, api_key, member_pid, out, create_workers, victim_samples, neighbour_samples, idle_seconds = sys.argv[1:]
create_workers = int(create_workers)
victim_samples = int(victim_samples)
neighbour_samples = int(neighbour_samples)
idle_seconds = int(idle_seconds)
api_url = api_url.rstrip("/")
metrics_url = metrics_url.rstrip("/")
headers = {"Authorization": f"Bearer {api_key}", "Accept": "application/json"}
if urllib.parse.urlsplit(api_url).scheme == "https":
    context = ssl.create_default_context()
else:
    context = None

api_parts = urllib.parse.urlsplit(api_url)
metrics_parts = urllib.parse.urlsplit(metrics_url)
if api_parts.scheme not in {"http", "https"} or not api_parts.hostname:
    raise SystemExit("tenant-scale: --api-url must be an HTTP(S) URL")
if metrics_parts.scheme not in {"http", "https"} or not metrics_parts.hostname:
    raise SystemExit("tenant-scale: --metrics-url must be an HTTP(S) URL")
if metrics_parts.path.rstrip("/") != "/internal/metrics" or metrics_parts.query or metrics_parts.fragment:
    raise SystemExit("tenant-scale: --metrics-url must be /internal/metrics without query or fragment")
if api_parts.hostname.lower().rstrip(".") != metrics_parts.hostname.lower().rstrip("."):
    raise SystemExit("tenant-scale: API and metrics URLs must identify the same member host")
try:
    metrics_address = ipaddress.ip_address(metrics_parts.hostname)
except ValueError:
    metrics_hostname = metrics_parts.hostname.lower().rstrip(".")
    if not (metrics_hostname.endswith((".internal", ".local")) or "." not in metrics_hostname):
        raise SystemExit("tenant-scale: metrics URL must identify a private member endpoint")
else:
    if not (metrics_address.is_private or metrics_address.is_loopback):
        raise SystemExit("tenant-scale: metrics URL must identify a private member endpoint")

def request(method, url, body=None):
    request_headers = dict(headers)
    payload = None
    if body is not None:
        payload = json.dumps(body, separators=(",", ":")).encode()
        request_headers["Content-Type"] = "application/json"
    req = urllib.request.Request(url, data=payload, headers=request_headers, method=method)
    started = time.monotonic()
    try:
        with urllib.request.urlopen(req, timeout=30, context=context) as response:
            response_body = response.read()
            return response.status, response_body, time.monotonic() - started
    except urllib.error.HTTPError as error:
        return error.code, error.read(), time.monotonic() - started

def percentile(values, fraction=0.99):
    if not values:
        raise RuntimeError("no latency samples were captured")
    ordered = sorted(values)
    index = min(len(ordered) - 1, math.ceil(fraction * len(ordered)) - 1)
    return ordered[index]

def proc_cpu_seconds(pid):
    line = pathlib.Path(f"/proc/{pid}/stat").read_text(encoding="utf-8")
    rest = line[line.rfind(")") + 2:].split()
    # After comm, fields start at state (field 3), so utime (14) is index 11.
    return (int(rest[11]) + int(rest[12])) / os.sysconf(os.sysconf_names["SC_CLK_TCK"])

def proc_starttime(pid):
    line = pathlib.Path(f"/proc/{pid}/stat").read_text(encoding="utf-8")
    rest = line[line.rfind(")") + 2:].split()
    # starttime is field 22, i.e. index 19 after the comm field.
    return rest[19]

def cgroup_limits(pid):
    lines = pathlib.Path(f"/proc/{pid}/cgroup").read_text(encoding="utf-8").splitlines()
    relative = next((line.split("::", 1)[1] for line in lines if line.startswith("0::")), None)
    if relative is None:
        raise RuntimeError("member is not in a cgroup-v2 scope")
    root = pathlib.Path("/sys/fs/cgroup") / relative.lstrip("/")
    cpu = (root / "cpu.max").read_text(encoding="utf-8").split()
    memory = (root / "memory.max").read_text(encoding="utf-8").strip()
    if len(cpu) != 2 or cpu[0] == "max":
        raise RuntimeError("member cpu.max is unbounded")
    if cpu != ["200000", "100000"]:
        raise RuntimeError(f"member cpu.max is {' '.join(cpu)}, expected 200000 100000")
    cpu_ocpu = int(cpu[0]) / int(cpu[1])
    memory_bytes = int(memory) if memory != "max" else -1
    if abs(cpu_ocpu - 2.0) > 1e-9 or memory_bytes != 12 * 1024**3:
        raise RuntimeError(f"member cgroup is {cpu_ocpu:g} OCPU/{memory_bytes} bytes, expected 2/12GiB")
    return cpu_ocpu, memory_bytes

def get_path(path):
    status, body, elapsed = request("GET", api_url + path)
    if status != 200:
        raise RuntimeError(f"GET {path} returned HTTP {status}: {body[:200]!r}")
    return elapsed, body

def get_json(path):
    _, body = get_path(path)
    try:
        return json.loads(body)
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise RuntimeError(f"GET {path} did not return JSON: {error}") from error

try:
    cpu_ocpu, memory_bytes = cgroup_limits(member_pid)
    member_starttime = proc_starttime(member_pid)
    health = get_json("/api/v1/health")
    if health.get("healthy") is not True or not health.get("region"):
        raise RuntimeError(f"GET /api/v1/health was not a healthy member response: {health!r}")
    regions = get_json("/api/v1/regions")
    if regions.get("healthy") is not True or regions.get("region") != health.get("region"):
        raise RuntimeError(f"GET /api/v1/regions was not the serving member response: {regions!r}")
    metrics_before_status, metrics_before_body, _ = request("GET", metrics_url)
    if metrics_before_status != 200:
        raise RuntimeError(f"GET metrics returned HTTP {metrics_before_status}")
    metrics_before = metrics_before_body.decode("utf-8", "replace")
    run_id = str(time.time_ns())
    prefix = f"e16-{run_id}-"
    names = [f"{prefix}{index:06d}" for index in range(100000)]
    placement = [health["region"]]

    def create(name):
        status, body, elapsed = request("POST", api_url + "/api/v1/databases", {"name": name, "placement": placement})
        if status == 201:
            try:
                record = json.loads(body)
            except (UnicodeDecodeError, json.JSONDecodeError):
                return 599, elapsed, b"create response was not JSON"
            if record.get("name") != name or record.get("deleted") is not False:
                return 599, elapsed, b"create response did not identify the created database"
        return status, elapsed, body[:200]

    started = time.monotonic()
    with concurrent.futures.ThreadPoolExecutor(max_workers=create_workers) as pool:
        creates = list(pool.map(create, names))
    wall_create = time.monotonic() - started
    create_latencies = [elapsed for status, elapsed, _ in creates if status == 201]
    create_errors = [status for status, _, _ in creates if status != 201]
    victim_name, neighbour_name = names[0], names[1]
    # A 201 alone is not a durable proof: read both concrete records back from
    # the member and verify the list contains every run-owned database.
    victim_record = get_json("/api/v1/databases/" + urllib.parse.quote(victim_name, safe="-"))
    neighbour_record = get_json("/api/v1/databases/" + urllib.parse.quote(neighbour_name, safe="-"))
    if victim_record.get("name") != victim_name or neighbour_record.get("name") != neighbour_name:
        raise RuntimeError("member GET did not return the created victim and neighbour records")
    listed = get_json("/api/v1/databases").get("databases")
    if not isinstance(listed, list):
        raise RuntimeError("member database listing did not return a databases array")
    listed_names = {record.get("name") for record in listed if isinstance(record, dict)}
    verified_count = sum(name in listed_names for name in names)
    if verified_count != len(names):
        raise RuntimeError(f"member database listing verified {verified_count} of {len(names)} run-owned databases")
    victim_path = "/api/v1/databases/" + urllib.parse.quote(victim_name, safe="-") + "/collections"
    neighbour_path = "/api/v1/databases/" + urllib.parse.quote(neighbour_name, safe="-") + "/collections"

    def samples(path, count, workers=32):
        def one(_):
            status, _, elapsed = request("GET", api_url + path)
            return status, elapsed
        with concurrent.futures.ThreadPoolExecutor(max_workers=workers) as pool:
            result = list(pool.map(one, range(count)))
        errors = [status for status, _ in result if status != 200]
        return [elapsed for status, elapsed in result if status == 200], errors

    solo, solo_errors = samples(victim_path, victim_samples)
    loaded_victim, loaded_neighbour = [], []
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        victim_future = pool.submit(samples, victim_path, victim_samples)
        neighbour_future = pool.submit(samples, neighbour_path, neighbour_samples)
        loaded_victim, victim_errors = victim_future.result()
        loaded_neighbour, neighbour_errors = neighbour_future.result()
    cpu_before = proc_cpu_seconds(member_pid)
    idle_started = time.monotonic()
    time.sleep(idle_seconds)
    idle_elapsed = time.monotonic() - idle_started
    cpu_after = proc_cpu_seconds(member_pid)
    if proc_starttime(member_pid) != member_starttime:
        raise RuntimeError("member PID was recycled during qualification")
    metrics_after_status, metrics_after_body, _ = request("GET", metrics_url)
    if metrics_after_status != 200:
        raise RuntimeError(f"final GET metrics returned HTTP {metrics_after_status}")
    metrics_after = metrics_after_body.decode("utf-8", "replace")
    required_metrics = ["terrabase_ready", "terrabase_requests_total", "terrabase_request_duration_seconds", "terrabase_active_members"]
    def metric_samples(text, metric):
        allowed = {metric}
        if metric == "terrabase_request_duration_seconds":
            allowed.update({metric + suffix for suffix in ("_bucket", "_count", "_sum")})
        return sum(
            1
            for line in text.splitlines()
            if line and not line.startswith("#")
            and line.split("{", 1)[0].split(None, 1)[0] in allowed
        )

    observed_before = {metric: metric_samples(metrics_before, metric) for metric in required_metrics}
    observed = {metric: metric_samples(metrics_after, metric) for metric in required_metrics}
    missing = [metric for metric in required_metrics if observed_before[metric] < 1 or observed[metric] < 1]
    cpu_seconds = max(0.0, cpu_after - cpu_before)
    solo_p99 = percentile(solo)
    loaded_p99 = percentile(loaded_victim)
    evidence = {
        "schema": "terrabase.e16.tenant-scale/v1", "qualification": "E-16", "status": "passed",
        "mode": "live-member", "transport": "member-http",
        "subject": {"database_count": 100000, "created_count": len(create_latencies), "verified_count": verified_count, "skipped": 0, "placeholder_databases": 0, "storage": "sqlite", "synthetic": False, "member_api": True},
        "envelope": {"cpu_ocpu": cpu_ocpu, "memory_gib": memory_bytes / 1024**3, "cgroup_verified": True},
        "metrics": {"source": "member-prometheus", "endpoint": metrics_url, "scrapes": 2, "required": required_metrics, "observed_before": observed_before, "observed": observed, "missing": missing},
        "api_paths": {"health": "/api/v1/health", "regions": "/api/v1/regions", "create_database": "/api/v1/databases", "get_database": "/api/v1/databases/" + victim_name, "victim_probe": victim_path, "neighbour_probe": neighbour_path},
        "create": {"requests": 100000, "successes": len(create_latencies), "p99_seconds": percentile(create_latencies), "wall_seconds": wall_create},
        "idle": {"seconds": idle_elapsed, "cpu_seconds": cpu_seconds, "cpu_ratio": cpu_seconds / idle_elapsed, "approximately_zero": cpu_seconds / idle_elapsed <= 0.01},
        "victim": {"solo_p99_seconds": solo_p99, "solo_requests": len(solo), "under_neighbour_p99_seconds": loaded_p99, "loaded_victim_requests": len(loaded_victim), "ratio": loaded_p99 / solo_p99, "adversarial_neighbour": True, "neighbour_requests": len(loaded_neighbour)},
        "run": {"executed": True, "errors": len(create_errors) + len(solo_errors) + len(victim_errors) + len(neighbour_errors), "member_pid": str(member_pid), "member_starttime": member_starttime, "create_error_statuses": create_errors[:20], "solo_error_statuses": solo_errors[:20], "victim_error_statuses": victim_errors[:20], "neighbour_error_statuses": neighbour_errors[:20]},
    }
    pathlib.Path(out).write_text(json.dumps(evidence, indent=2) + "\n", encoding="utf-8")
except Exception as error:
    print(f"tenant-scale: qualification failed before a valid receipt: {error}", file=sys.stderr)
    raise SystemExit(1)
PY
  validate "$out"
}

command=${1:-}
shift || true
case "$command" in
  validate) validate "$@" ;;
  qualify) qualify "$@" ;;
  *) usage ;;
esac
