# C# TerraBase HTTPS acceptance

Run the entire local acceptance harness from the repository root:

```sh
nice -n 19 make csharp-smoke
nice -n 19 python3 https-document/acceptance.py --output bench/csharp-baseline/NEW_RUN_ID
```

`release.json` pins a published TerraBase release by exact tag, source revision
and SHA-256. The harness downloads through `ORG_READ_TOKEN` (CI) or an existing
`fj` login (local), verifies the hash before executing, provisions fresh real
TerraBase storage, and removes temporary credentials, certificate, binary and
data on exit. Smoke commands create fresh run subdirectories under `bench/csharp-smoke/`,
preserving prior success and failure evidence. Explicit `--output` paths must
still be fresh. The current artifact is a Linux amd64 self-contained .NET JIT
executable; no .NET SDK is needed. OpenSSL and Python are required.

`main.rs` uses `/v1/projects/{project}/databases/{database}/collections/{collection}`
and `…/transactions` with `tb_` keys. Database names address the data API;
database UUIDs scope keys. The old Rust tenant/shard routes, sibling responses
and `tbk1` keys are rejected.

The driver runs five workloads at concurrency 1, 4 and 16:

| Workload | Operation and validation |
| --- | --- |
| Point read | GET a deterministic seeded document; check id, version, state and complete JSON |
| Point write | Conditional-create PUT a new document; require HTTP 201, check durable receipt and read back every write |
| Query | Filter even seed ranks and seed payload, sort rank descending, check exact top four results |
| Transactions | Two conditional-create PUTs with new IDs in one atomic request; check both results, durable receipt and every document |
| Mixed 90/10 | Exactly nine seeded reads then one conditional-create point write, repeated; require HTTP 201 and read back every write |

Each worker sends 100 operations per workload (10 for smoke), without retries.
Duplicate concurrency levels are rejected. Each configured concurrency level
is limited to 100,000 total samples per workload to bound sample retention in memory.
Write IDs are namespaced by workload, concurrency, worker, operation and
transaction slot, with no reuse across the entire run. All writes use
`If-None-Match: *` (transaction puts use `if_none_match: "*"` for both documents),
keeping the workload consistently insert-only across cases.
HTTP/1.1 keepalive clients share a connection pool. Latency covers request send,
complete response read, JSON parsing and validation. Construction and final
readbacks are excluded. Every write is checked after its workload case.
Throughput divides successful logical operations by
whole-workload elapsed time. One transaction is one logical operation with two
writes. Failed samples remain in evidence; percentiles include successful
samples only. The harness separately verifies transaction rollback.

For a separately provisioned endpoint, use `config.example.json` with absolute
input paths and a private token file:

```sh
nice -n 19 https-document/target/release/terrabase-https-document-point-read \
  --config workload.json --token-file document.key --output bench/UNIQUE_RUN
```

The executable retains its historical name for compatibility, but its old
config and output contracts are replaced. `endpoint_override` selects a static
HTTPS origin. With `null`, the resolver asks Atlas for the database placement,
selects the first ranked endpoint with read/write capability, validates the
origin and pins it for this invocation. Discovery uses platform CA trust;
document traffic trusts only `ca_pem_path`. Redirects are disabled and bearer
keys never enter evidence.

`receipt.json` contains status, latency percentiles in nanoseconds, throughput,
errors, readback check/error counts, `write_mode: "insert_only"`, release and binary hashes; `samples.jsonl` preserves every attempt;
`summary.md` gives a comparison table. The harness adds `environment.json` with
release, host, CPU affinity/priority, fixture and server settings. Worktree
dirtiness includes unstaged, staged and untracked source changes, captured
before building or creating evidence. A harness failure marks the receipt and
summary failed even if the driver completed successfully; its metrics remain
available for diagnosis. Smoke timings are observations, not performance gates. The server labels writes
`local_durable`, but the pinned SQLite WAL `synchronous=NORMAL` settings do not
establish power-loss durability. This baseline does not make a durability claim
beyond the server contract.
