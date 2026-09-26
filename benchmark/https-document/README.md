# TerraBase HTTPS document point read

This driver measures the real packaged TerraBase client endpoint. It sends
1,000 serial authenticated `GET` requests for one already committed document.
Each request uses a new verifying Rust TLS client and HTTP/1.1 connection.
The timer covers the TLS handshake, HTTP request and response, full body read,
JSON parse, and exact equality with the expected response. Client and request
construction, pacing, fixture setup, and the preliminary correctness read are
untimed.

The fixture owner must provision the server, initialize the local v1 store,
commit the document, and supply the CA PEM, scoped bearer token, and full
expected point-read JSON. The Fast integration caller in
`../terrabase/crates/tb-server/tests/client_process.rs` demonstrates this path:
initializer → packaged server → durable HTTPS commit → authenticated point read.
Its sample document includes field ID 3 with typed `i64` value `"73"`. Capture
the expected response only after verifying that seed. The driver requires a
version 1 `value` response with one sibling and compares every JSON value,
including the sibling fields, on every read.

Copy `config.example.json` and replace its absolute paths, endpoint IDs, and
source revision. The source manifest is a caller-supplied file describing the
server source checkout; the receipt hashes it but does not independently prove
the claimed revision. Keep the bearer token only in a separate private file.
The driver never prints or records it.

`inter_request_delay_ms` defaults to 20 and may be set from 10 to 1,000.
After every timed read except the last, the driver waits at least this long
before starting the next. At the default, configured offered load is at most
50 requests/s; network and server latency can lower it. The fixture's scoped
read key must have a rate and burst sufficient for the chosen pace. A 429 is
recorded as a failed sample without a retry. The receipt and Markdown table
show the delay and configured offer cap; this is a paced latency workload,
not a peak-throughput result.

From this directory, after a lockfile and release binary have been built:

```sh
./target/release/terrabase-https-document-point-read \
  --config /absolute/path/to/workload.json \
  --token-file /absolute/path/to/token.txt \
  --bench-dir /absolute/path/to/ts-benchmark/bench \
  --run-id unique-run-id
```

The driver creates `bench/https-document-point-read/<run-id>/` exclusively.
It writes `samples.jsonl`, `receipt.json`, and `summary.md`. A failed untimed
preflight leaves a failed receipt with zero timed samples. Any failed timed
read remains in JSONL; all 1,000 timed attempts complete, then the process
exits nonzero. The Markdown table reports only correct-read latency and shows
the failure count. A run from this driver alone is not an acceptance result;
qualifying results require the repository's verified Firecracker environment
and its environment receipt.
