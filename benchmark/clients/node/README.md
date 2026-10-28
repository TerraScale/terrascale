# Node SDK / raw HTTPS parity

Transport counters qualified with isolated TLS fixtures; no latency or production claim. Use Node 22 or 24 and clean
SDK source `b1bfe76254d8cc3e8e0aaa1a87dd599e7af4efe0` (0.0.18). In a separate
SDK checkout run `nice -n 19 npm ci --ignore-scripts`, using its committed lockfile
(Undici 7.30.1). Stage this runner as `benchmark/bench/node-parity.mjs` inside that
checkout so package self-reference imports that exact SDK. No npm release or pack
is needed. The identity row hashes imported SDK source, without logging URLs.

Run through `ts-heavy-any nice -n 19`: `node <staged runner> --credentials PRIVATE.json
--out NEW.jsonl`. The private file contains api_key/project. Output must be new.
Set `TS_PEERS_FILE` to the ts-orch peers file; regions are discovered from it;
gru/sjc/fra are required. Setup creates one owned all-regions database and two
documents. Cleanup deletes that exact database; account/inbox cleanup is external.

Each region runs three alternating repeats, 256/4096 B, c=1/16, and matched
raw-h1/sdk-h1/raw-h2/sdk-h2 lanes. Each lane owns a persistent global Undici Agent;
SDK transport, token-provider interface and read retry policy remain unchanged.
The benchmark owns the API-key exchange via oidc.token/client_secret_post. The
JS SDK has no built-in API-key exchange or 401 refresh. Raw/SDK operation deadlines
are 60 s. Only exact HTTPS443 setup/document paths get the region pin, never auth.

After readiness, every warmup/measured attempt must be 200 with one matching
region header and correct document id/version/state/body. Socket ALPN send counts
must match fetch attempts and started operations, rejecting hidden retries and
H2 fallback. TLS controls confirmed attempt/protocol gates, duplicate-header rejection and auth pin isolation.
Cells warm for 2 s, measure 5 s and drain for at most 30 s. Only operations wholly
inside the window count; a uniform 16,384-sample reservoir bounds retained timings.
TCP connect counts include readiness. Empty/failed/stuck/mismatched cells are
emitted before rejection. No live execution has occurred. Default H2 Agents opened 16 connections during the fixture burst; subsequent reads reused them.
