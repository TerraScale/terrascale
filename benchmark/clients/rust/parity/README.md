# Rust SDK / raw HTTPS parity

End-to-end point-read round trips through the shipped Rust SDK and through raw reqwest,
on one owned synthetic database. It is separate from the legacy `bench-client` preflight
in the parent directory, which it does not change.

## Source identity

ts-rust `v0.0.5`, commit `38a33829dd7099f22783ba79d8b187450328a59b`, cloned over HTTPS
from `github.com/TerraScale/ts-rust`. `stage.sh NEW-DIR` makes a fresh checkout, refuses
a dirty or different `HEAD`, copies these `.rs` files to `examples/api_parity/`, and builds
with that commit's committed `Cargo.lock` (`--locked`; reqwest 0.12.28, rustls 0.23.46,
tokio 1.53.3) and toolchain (`rust-toolchain.toml`, 1.98.1). The runner uses only the
SDK's existing dependencies and dev-dependencies; nothing is added to this repository's
dependency graph. The identity row records the SDK version and commit, the SHA-256 of the
SDK `Cargo.lock` and of each runner file.

```sh
TS_HEAVY_HOST=delta ts-heavy-any nice -n 19 bash benchmark/clients/rust/parity/stage.sh \
  "$HOME/remote-builds/rust-parity/stage" --repo terrascale --branch fix/api-rust-parity
"$HOME/remote-builds/rust-parity/stage/target/release/examples/api_parity" \
  --credentials PRIVATE.json --out NEW.jsonl
```

`stage.sh` runs rustfmt, clippy (`-D warnings`), the synthetic controls in `controls.rs`,
the release build and `cli-control.sh`. None of them reads credentials or makes a request.

## Contract

- `--credentials` names a private JSON file with `api_key` and `project`. `--out` must be
  new; it is created with mode 0600 and never overwritten.
- Regions come from every `<node> <region> <netbird-ipv4>` line of `TS_PEERS_FILE`;
  gru, sjc and fra are required. The alphabetically first region is home.
- Setup, through the public SDK pinned to home, creates `api-perf-<ulid>` with home plus
  `allRegions`, collection `bench`, and two ULID documents whose compact JSON is exactly
  256 and 4096 bytes. The database name is written before any mutation; its ID once known.
- Per region: three repeats, alternating lane order, of 256/4096 B x c=1/16 x `raw-h1`,
  `raw-h2`, `sdk`. Each cell owns new persistent clients, a readiness phase (60 s; 10 s per
  try), 2 s warmup, a 5 s window and at most 30 s drain. Operations started in the window
  keep their latency, drain included; throughput runs through the last measured completion.
  A uniform 16,384-sample reservoir bounds retained latencies. The whole run is bounded at
  30 minutes; setup at 60 s.
- Raw lanes: SDK transport settings (rustls TLS 1.3, web-PKI roots, no proxy, no redirects,
  30 s request, 10 s connect). `raw-h1` offers ALPN `http/1.1` only, `raw-h2` `h2` only.
  Each lane has its own cached exchange to `auth.terrascale.tech` (Basic key, form
  `grant_type=client_credentials` and `project`, as the SDK). Every answer must be 200 on the
  lane's protocol with exactly one `X-TerraBase-Region` equal to the pin, and the seed's id,
  version, `value` state and body.
- `sdk` lane: `Client::for_project_api_key` with an explicit endpoint and `region_pin`. API-key
  exchange, background refresh and the default retry policy are unchanged. The SDK rejects a
  missing, duplicate or different response region itself; the runner checks the typed id,
  version, state and body.
- The pin header is sent only on API requests, never to auth.
- Cleanup makes one bounded (30 s) SDK `delete_database` call on the exact owned database (by ID
  once known, otherwise by name) after success, setup failure, an uncertain create, any cell
  rejection, the run deadline or an interrupt. Acceptance means logical deletion; remote
  erasure is unverified. A 404 after an uncertain create requires reconciliation: the
  create may still commit. On failure or uncertainty the identity is kept in the output and in
  `NEW.jsonl.recovery.json` (0600) for reconciliation.
- Shutdown: the binary re-runs itself as a worker in its own process group, with a pipe from
  the supervisor as stdin. Ctrl-C or the supervisor's exit closes the pipe; the worker stops
  measuring and still runs cleanup. A signal sent straight to the worker's group, or SIGKILL
  of a whole cgroup, skips cleanup; the run and cleanup bounds still apply.

## Output and limitations

Rows: `identity`, `database_created`, `setup_failure`, `cell`, `database_deletion` and
`recovery`. A rejected cell (`readiness`, `operation_error`, `drain_timeout`, `empty`) is
written and stops the run. Error rows carry status and SDK error code, never server text.

- `sdk` `attempts` and `protocol` are `null`: v0.0.5 has no attempt or connection hook, so its
  retries, 401 refresh and ALPN choice are not observable. They are never inferred from raw.
- `sdk` uses the shipped ALPN default (h2 preferred). No row claims a matched protocol or zero
  retries for it.
- Raw `attempts` count runner-issued requests. reqwest's default protocol-NACK retry and
  hyper-util's resend of an unsent pooled request are unobservable in every lane.
- `tcp_connects` is `null` everywhere: reqwest 0.12.28 exposes no connection counter.

No live run has been made: setup, deletion, cleanup authorization and every latency or
throughput figure are unqualified. Results go to `benchmark/bench/`.
