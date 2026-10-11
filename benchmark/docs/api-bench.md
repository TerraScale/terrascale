# TS-832 API benchmark contract

Measures throughput and latency of the production TerraScale API
(`https://gru.api.terrascale.tech`) through the public SDKs (ts-go, ts-dotnet
JIT and Native AOT) plus one raw-HTTP baseline per language. One shared spec,
[workloads/api-bench.json](../workloads/api-bench.json), drives every client.
This is separate from the TS-761 capacity protocol in [protocol.md](protocol.md).

## Run it

```sh
nice -n 19 make api-bench CLUSTER_STATE=stable   # or CLUSTER_STATE=changing
```

`make api-bench` builds the three clients and runs `bin/api-bench`, which writes
`bench/results/<UTC date>-<host>-<ULID>.md` and `.json`. Runner flags:

| Flag | Meaning |
| --- | --- |
| `--cluster-state stable\|changing` | Required. `changing` (TS-803/TS-831 touching gru nodes) skips the 512 step and every 64 KB write step. Recorded in the report. |
| `--clients go,dotnet-jit,dotnet-aot` | Clients to run, sequentially. Default all three. |
| `--payload 4096` | Restrict to one payload size (repeatable). Default: the spec's sizes. |
| `--spec PATH` | Default `workloads/api-bench.json`. |

The runner refuses to run on `mariogk-desktop`.

## Credentials

The runner reads OCI Vault secret `ts-bench-api-key`, whose content is JSON:

```json
{"api_key": "tsk_<ULID>_<secret>", "project": "<project ULID>"}
```

It uses the host's OCI CLI profile (`~/.oci/config`), finds the secret by name
with OCI Resource Search, and reads its current version. The key is written to
a 0600 file in a fresh 0700 temp dir, which the clients receive by path. The
runner deletes the dir when it exits. No credentials file is hand-copied.

The key must be a project API key with `databases:read databases:write
documents:read documents:write` for a dedicated benchmark project. It never
appears on a command line, in an environment variable, log or result file.

## Scenarios

Payload = exact UTF-8 byte length of the document JSON
`{"g":<0..15>,"p":"<padding>"}`.

| Scenario | One operation |
| --- | --- |
| `auth.token` | Fresh API key → JWT client-credentials exchange (no cache). Every other scenario uses one cached JWT. |
| `doc.get` | Point read of a random seeded id in `seed_<payload>`. |
| `doc.insert` | Create a new document in `ins_<payload>` (server-generated ULID). |
| `doc.update` | Replace (PUT, no precondition) a random seeded id in `upd_<payload>`. |
| `doc.delete` | Delete a document the same worker just inserted. Only the delete call is timed. |
| `query.small` | Equality query `g == rand(0..15)` on `seed_256`, limit 10. |
| `txn.batch` | Atomic transaction of 2 creates of `<payload>` each in `txn_<payload>`. |
| `mixed.80-20` | 80% `doc.get`, 20% `doc.update` on `seed_<payload>`; op chosen by `i % 5 == 4` → update. |

Setup per client run (untimed): exchange the key for a JWT and create database
`bench-<ULID in lowercase>`, because TerraBase database names must match
`^[a-z][a-z0-9_-]{0,62}$`. Collections and seeds are created lazily, before the
first step that uses a payload size and never during a measured window: the
collections `seed_<payload>`, `upd_<payload>`, `ins_<payload>`, `txn_<payload>`,
then `seed_documents` documents with `g = i % 16` in `seed_<payload>` and
`upd_<payload>`. `query.small` reads `seed_256`; `auth.token` needs no seed.
Cleanup deletes the database on stdin EOF or setup failure.

SDK retries are disabled (ts-go `MaxRetries: -1`; ts-dotnet never retries) so
every request is measured once and server pushback shows as status codes.

Transports: `sdk` (public SDK), `raw-h2` / `raw-h1` (the language's own HTTP
client, pooled, HTTP/2 or HTTP/1.1 forced, same routes and JWT as the SDK).
Raw baselines run only for `raw_baseline.scenarios` × `raw_baseline.payload_bytes`.

## Sweep and safety (runner-owned)

For each scenario × payload × transport, the runner walks the concurrency
steps upwards in closed-loop mode (each worker issues its next op when the last
completes). The first step's p99 is the baseline. A step is **stopped** when
its error rate > `max_error_rate` or its p99 > `max_p99_ratio` × baseline; the
sweep then skips its higher steps. Clients also abort a step mid-way (checked
every second after ≥ 20 completed ops) on the same thresholds, which the runner
passes in each step command.

After a closed-loop sweep of an `open_loop.scenarios` entry, the runner runs
open-loop steps at each `fractions` × the best passing closed-loop req/s,
with in-flight cap = the highest passing concurrency. Arrival `i` is scheduled
at `start + i/rate`; latency is completion − **intended** start (no coordinated
omission). An arrival that finds the cap full is counted as status `dropped`.

Every step lasts `warmup_s` (unrecorded) + `step_s` (recorded), ≤ 60 s.

## Runner ↔ client protocol (JSON lines)

Client binaries: `bin/api-bench-go`, `clients/dotnet/api-bench/out/jit/api-bench`
and `clients/dotnet/api-bench/out/aot/api-bench`.

`<client> startup` initialises the SDK clients (no network), prints one
`{"type":"startup","init_ms":<float>}` line and exits 0. The runner times five
launches (wall ms, peak RSS from rusage) for the JIT vs AOT comparison.

`<client> run --spec PATH --credentials PATH` performs setup, then prints:

```json
{"type":"ready","client":"go","sdk":"ts-go","sdk_version":"<module version or git rev>","runtime":"go1.25.3","database":"bench-01k…","setup_ms":1234.5,"auth_ms":81.2}
```

It then reads step commands from stdin, one per line, and answers each with
exactly one result line:

```json
{"type":"step","scenario":"doc.get","transport":"sdk","payload_bytes":4096,"mode":"closed","concurrency":8,"rate":0,"warmup_s":2,"step_s":10,"abort_error_rate":0.01,"abort_p99_us":0}
```

`mode` is `closed` or `open` (`rate` = arrivals/s, `concurrency` = in-flight cap).
`abort_p99_us` 0 means no latency abort (baseline step).

```json
{"type":"result","scenario":"doc.get","transport":"sdk","payload_bytes":4096,"mode":"closed","concurrency":8,"rate":0,"start":"2026-10-11T12:00:00Z","duration_s":10.01,"ops":1234,"ok":1230,"errors":4,"error_rate":0.0032,"rps":122.9,"status":{"ok":1230,"429":4},"latency_us":{"p50":6100,"p90":7000,"p99":9100,"p999":15000,"max":21000,"mean":6400},"bytes_out":123456,"bytes_in":5123456,"cpu_s":1.2,"rss_mb":61.5,"stopped":""}
```

- `ops` counts every recorded arrival, including errors and drops; `rps` = `ok / duration_s`.
- `status` keys: `ok`, an HTTP status code as a string, `transport`, `timeout`, `canceled`, `dropped`, and `setup` (step skipped because its untimed seeding failed).
- Latency comes from an HDR histogram (1 µs … 60 s, 3 significant digits) of successful ops only.
- `bytes_out` / `bytes_in`: request and response body bytes counted by a wrapping HTTP handler during the recorded window.
- `cpu_s`: process user+system CPU during the recorded window; `rss_mb`: current resident set at step end.
- `stopped`: empty, or `error rate` / `p99` when the client aborted the step early.

On stdin EOF the client deletes its database and prints
`{"type":"done","cleanup":true,"cleanup_error":""}`, then exits 0. SIGINT/SIGTERM
only cut the running step short; the runner closes stdin to end the run.
Diagnostics go to stderr and never contain the API key, JWTs or document bodies.

## Results

`bench/results/<date>-<host>-<ULID>.json` holds host, UTC start/end, cluster
state, network probe (TCP connect, TLS handshake and `GET /healthz` min/median
over 10 tries), startup timings and every ready/result/done line.
The `.md` beside it has one table per scenario, with columns client, transport,
payload, mode, concurrency, req/s, p50, p90, p99, p99.9, max (ms), error %,
status codes, MB out/in, client CPU %, RSS MB, and stopped steps.
