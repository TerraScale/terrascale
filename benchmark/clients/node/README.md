# Node SDK / raw HTTPS parity

Use the pinned SDK source and committed lock described in [TRANSPORT.md](TRANSPORT.md).
Stage this directory under its `benchmark/bench/node/`; package self-reference imports
that exact SDK. Identity hashes all source-tree JavaScript, including tests.
Run via `ts-heavy-any nice -n 19 node parity.mjs --credentials PRIVATE.json --out NEW.jsonl`.
The private file contains api_key/project. Output must be new. `TS_PEERS_FILE` supplies
the ts-orch peer catalog; all discovered regions run, with gru/sjc/fra required.

Setup creates one owned all-regions database and two documents, under a 60 s deadline.
Cleanup distinguishes accepted logical deletion from absence; a 404 after an uncertain
create requires reconciliation. Remote erasure is unverified. Account/inbox
cleanup remains external. Repeated interrupts keep cleanup active.
Each region gets three alternating repeats, 256/4096 B, c=1/16, matched
raw-h1/sdk-h1/raw-h2/sdk-h2. Every cell owns a pool for readiness, warmup and measurement.
A cached token source per lane exchanges via oidc.token/client_secret_post; the JS SDK
has no built-in API-key exchange or 401 refresh. Raw/SDK read deadlines are 60 s.
Only exact HTTPS443 setup/document routes get a region pin, never auth.

Every warmup/measured attempt must be 200 with one matching response region header and
correct id/version/state/body. Socket ALPN sends must match attempts and operations,
rejecting retries and H2 fallback. TCP connects include readiness; empty/failed/stuck
cells are retained and rejected. There is no live execution or latency parity proof.
Cells warm 2 s, measure 5 s and drain up to 30 s. Starts inside the window retain their
full latency; throughput includes time through the final measured completion.
A uniform 16,384-sample reservoir bounds timings. `node window-control.mjs` checks
slow-tail retention, peer parsing and symlink invocation; `node control.mjs` checks
transport observation. Production setup/deletion and physical erasure remain unqualified.
