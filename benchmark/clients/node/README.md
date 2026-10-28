# Node SDK / raw HTTPS parity

Stage under the pinned SDK's `benchmark/bench/node/` with its unchanged lock; see
[TRANSPORT.md](TRANSPORT.md). Identity hashes all source JavaScript, including tests.
Run via `ts-heavy-any nice -n 19 node parity.mjs --credentials PRIVATE.json --out NEW.jsonl`.
The private file contains api_key/project; output must be new. Regions come from
`TS_PEERS_FILE`, with gru/sjc/fra required. Setup owns one all-regions database and
256/4096 B documents, under a 60 s deadline. Cleanup distinguishes logical deletion
from absence; a 404 after uncertain creation needs reconciliation. Physical erasure
is unverified; account/inbox cleanup remains external. Repeated signals keep cleanup active.

Each region runs three alternating repeats, c=1/16, raw-h1/sdk-h1/raw-h2/sdk-h2.
Each cell owns a pool across readiness, 2 s warmup, 5 s measurement and a 30 s drain.
Window starts retain full tails; throughput includes time to the final completion.
A uniform 16,384-sample reservoir bounds timings. Every attempt must be 200 with
one matching region header and correct document; sends must match attempts and
logical starts. Retries, H2 fallback and empty/failed/stuck cells reject the run.
A cached benchmark token provider per lane uses oidc.token/client_secret_post;
the JS SDK has no built-in key exchange/401 refresh. Raw/SDK read deadlines are 60 s.
Only exact HTTPS443 API routes get a pin. Run `node control.mjs` and
`node window-control.mjs` for offline controls; live setup/deletion remain unqualified.
