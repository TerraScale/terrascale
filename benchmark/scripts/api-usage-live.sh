#!/usr/bin/env bash
# Live keyed document GET cost on one ts-orch serving node: TerraBase process CPU per request at a fixed, bounded rate.
# Run from an admitted operator host, script on stdin:
#   ts-orch netbird ssh ... -- ... ubuntu@ts-<region>-1 bash -s -- <label> < benchmark/scripts/api-usage-live.sh
# Creates one synthetic organization (Enterprise plan, so QoS does not rate-limit it), project, database and admin API key
# on the local node, and deletes them afterwards. Prints JSON lines only; never prints credentials.
set -euo pipefail
# One function: bash -s reads all of it before any command can touch stdin.
main() {
label=${1:?usage: api-usage-live.sh <label>}
umask 077
work=$(mktemp -d /dev/shm/terrascale-usage-live-XXXXXX)
trap 'rm -rf -- "$work"' EXIT
container=$(join <(sudo -n ctr -n terrascale containers ls | awk '$2 ~ /terrascale\/terrabase@/ {print $1}' | sort) \
  <(sudo -n ctr -n terrascale tasks ls | awk '$3 == "RUNNING" {print $1}' | sort))
[[ -n "$container" && "$container" != *$'\n'* ]] || { echo 'expected one running TerraBase' >&2; exit 1; }
pid=$(sudo -n ctr -n terrascale tasks ls | awk -v c="$container" '$1 == c {print $2}')
environment=$(sudo -n cat "/proc/$pid/environ" | tr '\0' '\n')
secret_file=$(sed -n 's/^TERRABASE_PEER_SECRET_FILE=//p' <<<"$environment")
peers_file=$(sed -n 's/^TS_PEERS_FILE=//p' <<<"$environment")
node=$(sed -n 's/^TS_NODE=//p' <<<"$environment")
unset environment
[[ -n "$secret_file" && -n "$peers_file" && -n "$node" ]] || { echo 'missing ts-orch identity' >&2; exit 1; }
{ printf 'X-TerraBase-Peer-Credential: '; sudo -n cat "/proc/$pid/root$secret_file"; printf '\n'; } > "$work/peer"
address=$(sudo -n cat "/proc/$pid/root$peers_file" | awk -v n="$node" '$1 == n {print $3}')
[[ -n "$address" && "$address" != *$'\n'* ]] || { echo 'local node is ambiguous in peers' >&2; exit 1; }
python3 -I - "$work" "$pid" "$address" "$label" <<'PY'
import http.client, json, math, multiprocessing, os, pathlib, secrets, statistics, subprocess, sys, time

work, pid, address, label = sys.argv[1:]
RATE, WORKERS, LOAD_S, IDLE_S, REPEATS = 1000, 4, 10, 5, 3


def emit(**row):
    row.update(label=label, at=time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()))
    print(json.dumps(row), flush=True)


def ulid():
    n = (int(time.time() * 1000) << 80) | secrets.randbits(80)
    return "".join("0123456789ABCDEFGHJKMNPQRSTVWXYZ"[(n >> i) & 31] for i in range(125, -1, -5))


user = ulid()
pathlib.Path(work, "user").write_text("X-TerraBase-User-Id: " + user + "\n")


# The internal listener (h2c) as the platform operator; `user` adds the acting synthetic user.
def admin(method, path, body=None, user=False):
    command = ["curl", "-sS", "--http2-prior-knowledge", "-m", "60", "-o", "-", "-w", "\n%{http_code}", "-X", method,
               "-H", "@" + os.path.join(work, "peer")]
    if user:
        command += ["-H", "@" + os.path.join(work, "user")]
    if body is not None:
        command += ["-H", "Content-Type: application/json", "--data-binary", "@-"]
    command.append(f"http://{address}:8081{path}")
    done = subprocess.run(command, input=None if body is None else json.dumps(body).encode(), capture_output=True, check=True)
    text, _, status = done.stdout.decode().rpartition("\n")
    return int(status), (json.loads(text) if text.strip() else None)


def expect(result, *codes):
    if result[0] not in codes:
        raise RuntimeError(f"unexpected HTTP {result[0]}")
    return result[1]


def cpu():
    fields = pathlib.Path(f"/proc/{pid}/stat").read_text().rsplit(")", 1)[1].split()
    return (int(fields[11]) + int(fields[12])) / os.sysconf("SC_CLK_TCK")


def worker(key, path, rate, seconds, results):
    connection = http.client.HTTPConnection("127.0.0.1", 8080, timeout=20)
    headers = {"Authorization": "Bearer " + key}
    samples, errors, interval = [], 0, 1 / rate
    start = next_at = time.perf_counter()
    while next_at - start < seconds:
        delay = next_at - time.perf_counter()
        if delay > 0:
            time.sleep(delay)
        began = time.perf_counter()
        connection.request("GET", path, headers=headers)
        response = connection.getresponse()
        response.read()
        samples.append((time.perf_counter() - began) * 1e6)
        errors += response.status != 200
        next_at += interval
    results.put((samples, errors))


# The client listener with the run's key; retries while a new database is still being placed (404/503).
def client(method, path, key, body=None, seconds=300):
    deadline = time.monotonic() + seconds
    while True:
        connection = http.client.HTTPConnection("127.0.0.1", 8080, timeout=60)
        connection.request(method, path, body, {"Authorization": "Bearer " + key, "Content-Type": "application/json"})
        response = connection.getresponse()
        response.read()
        connection.close()
        if response.status in (200, 201):
            return
        if response.status not in (404, 503) or time.monotonic() >= deadline:
            raise RuntimeError(f"{method} client request: HTTP {response.status}")
        time.sleep(1)


tenant = {}
try:
    status, ready = admin("GET", "/readyz")
    emit(kind="metadata", node=os.uname().nodename, revision=(ready or {}).get("revision"), status=status, nproc=os.cpu_count(),
         load=os.getloadavg(), rate=RATE, workers=WORKERS, load_s=LOAD_S, idle_s=IDLE_S)
    tenant["organization"] = expect(admin("POST", "/v1/management/organizations", {"name": "perf usage live"}, user=True), 201)["id"]
    tenant["management"] = f"/v1/management/organizations/{tenant['organization']}"
    expect(admin("PUT", tenant["management"] + "/plan", {"plan": "enterprise"}, user=True), 200)
    tenant["project"] = expect(admin("POST", tenant["management"] + "/projects", {"name": "perf"}, user=True), 201)["id"]
    tenant["management"] += f"/projects/{tenant['project']}"
    name = "perf-" + secrets.token_hex(4)
    tenant["database"] = expect(admin("POST", tenant["management"] + "/databases", {"name": name}, user=True), 201)["id"]
    created = expect(admin("POST", "/admin/api-keys", {"name": "perf usage live", "scopes": ["documents:read", "documents:write"],
                                                      "organizationId": tenant["organization"], "projectId": tenant["project"],
                                                      "databaseId": tenant["database"]}), 201)
    tenant["key_id"], key = created["id"], created["secret"]
    # The acting user is a synthetic identifier, not a credential: retained so an interrupted run can be cleaned up.
    emit(kind="tenant", user=user, **{k: v for k, v in tenant.items() if k != "management"})
    database = f"/v1/projects/{tenant['project']}/databases/{name}"
    client("POST", database + "/collections", key, json.dumps({"name": "perf"}))
    paths = {}
    for size, total in (("small", 256), ("medium", 4096)):
        paths[size] = database + "/collections/perf/documents/by-key?id=" + ulid()
        client("PUT", paths[size], key, json.dumps({"value": "x" * (total - len('{"value":""}'))}))
    context = multiprocessing.get_context("fork")
    for repeat in range(1, REPEATS + 1):
        for size, path in paths.items():
            idle_cpu, began = cpu(), time.monotonic()
            time.sleep(IDLE_S)
            idle_rate = (cpu() - idle_cpu) / (time.monotonic() - began)
            results = context.Queue()
            processes = [context.Process(target=worker, args=(key, path, RATE / WORKERS, LOAD_S, results)) for _ in range(WORKERS)]
            load_cpu, began = cpu(), time.monotonic()
            for process in processes:
                process.start()
            gathered = [results.get(timeout=LOAD_S + 60) for _ in processes]
            elapsed, used = time.monotonic() - began, cpu() - load_cpu
            for process in processes:
                process.join()
            samples = sorted(v for part, _ in gathered for v in part)
            requests, errors = len(samples), sum(e for _, e in gathered)
            emit(kind="cell", size=size, repeat=repeat, requests=requests, errors=errors, ops_per_s=requests / elapsed,
                 cpu_s=used, idle_cpu_per_s=idle_rate, cpu_us_per_op=(used - idle_rate * elapsed) / requests * 1e6,
                 p50_us=statistics.median(samples), p99_us=samples[math.ceil(len(samples) * .99) - 1], load=os.getloadavg())
finally:
    steps = []
    if "key_id" in tenant:
        steps.append(("revoke key", "/admin/api-keys/" + tenant["key_id"], False))
    if "database" in tenant:
        steps.append(("delete database", tenant["management"] + "/databases/" + tenant["database"], True))
    if "project" in tenant:
        steps.append(("delete project", tenant["management"], True))
    if "organization" in tenant:
        steps.append(("delete organization", "/v1/management/organizations/" + tenant["organization"], True))
    # Every step is retried and attempted: a busy database must not leave the project or organization behind.
    for step, path, acting in steps:
        for attempt in range(20):
            try:
                status = admin("DELETE", path, user=acting)[0]
            except subprocess.CalledProcessError as error:
                status = f"curl {error.returncode}"
            emit(kind="teardown", step=step, attempt=attempt + 1, status=status)
            if status in (204, 404):
                break
            time.sleep(15)
    if "organization" in tenant:
        emit(kind="teardown", step="organization gone", status=admin("GET", "/v1/management/organizations/" + tenant["organization"], user=True)[0])
PY
}
main "$@"
