# Auth fresh-read latency baseline — 2026-10-27

Auth `9682f0ec3ffb934945dde49c277ccaef6ba08d0a`; dashboard `d5b0e7b87ebd13d6753c9109a578bc80a750bcc7`. GRU node `ts-gru-2`, reached through SSH alias `ts-gru-1`. Image digests and request/trace IDs are in [the sanitized records](2026-10-27-auth-read-before.jsonl).

Three sequential signed-in full-body `GET /support` requests from delta; redirects refused, no-cache request, existing shared session read without modification. No sign-in, credential export or forced token refresh. The parent’s initial three samples at 22:24:51–53Z are preserved separately; the correlated samples below ended at 22:28:47Z. Each correlated sample captured all eight auth spans immediately after its response because the runtime stdout file rotates.

All durations are milliseconds. Independent calls overlap; summed auth time exceeds elapsed auth wait. Three samples describe these requests and do not establish a production p95.

| Correlated sample | HTTP | Page server time | Auth elapsed wait | Summed auth calls | Full body client time |
|---|---:|---:|---:|---:|---:|
| 1 | 200 | 633.6 | 606.2 | 2075.6 | 727.8 |
| 2 | 200 | 727.3 | 708.2 | 2240.8 | 818.4 |
| 3 | 200 | 879.8 | 784.0 | 2629.8 | 988.3 |

| Auth operation | Repeat 1 | Repeat 2 | Repeat 3 | HTTP |
|---|---:|---:|---:|---|
| `terrascale/oauth/userinfo` | 561.9 | 666.7 | 778.7 | 200 |
| `v1/terrascale/organizations` | 154.2 | 335.4 | 355.2 | 200 |
| `v1/terrascale/organizations/:value/projects (request 1)` | 209.1 | 294.6 | 319.6 | 200 |
| `terrascale/.well-known/openid-configuration` | 6.3 | 5.0 | 3.5 | 200 |
| `v1/terrascale/organizations/:value/members` | 451.1 | 371.7 | 410.8 | 200 |
| `v1/terrascale/organizations/:value/ownership-transfer` | 342.4 | 253.0 | 339.1 | 404 (no pending transfer) |
| `terrascale/.well-known/:value` | 2.8 | 1.9 | 3.6 | 200 |
| `v1/terrascale/organizations/:value/projects (request 2)` | 347.8 | 312.6 | 419.3 | 200 |

Project requests are numbered by start time within each page; organization identifiers are omitted. `terrascale/.well-known/:value` is the dashboard’s redacted discovery-key URL. No token POST occurred in this warm-session workload, so this is not a token-exchange baseline. Cold exchange measurement remains separate from these read timings.

The measured long calls are fresh identity/management reads, while discovery responses take only a few milliseconds. Source inspection found a serial ancestor-validation walk and unnecessary explicit project grants for owners/admins; #758 batches independent ancestors and skips grants that cannot raise the inherited role. These measurements locate the slow calls; they do not attribute all of their duration to that walk. No baseline storage spans existed to establish that attribution.

Reproduce the page samples with `nice -n19 python3 benchmark/scripts/auth-dashboard-probe.py`; keep the shared session file mode 0600 and read-only to this probe. Correlate its request IDs or client trace IDs with the serving dashboard’s `ServerTiming` records immediately after each response. Preserve only fixed operation names, statuses, timing fields and correlation IDs, never headers, bodies, resource names or credentials.

After measurements: pending deployment of #758. No improvement or sub-200 ms claim is made.
