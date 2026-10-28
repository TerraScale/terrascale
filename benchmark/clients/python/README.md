# Async Python SDK / raw HTTPS parity

Unqualified runner; no latency or production claim. Use the merged Python SDK 0.0.5
at `49ea36ee9dfc005b1126e4baa68648b0e8fbd221`. Install that clean checkout into a
private venv. Extract the runtime entries (anyio, certifi, exceptiongroup, h11, h2,
hpack, httpcore, httpx, hyperframe, idna, typing-extensions), including hashes, from
its `requirements-ci.txt`; install that subset with `--require-hashes`. Keep the
clean checkout on `PYTHONPATH=<SDK>/src`. The full CI lock also includes development
dependencies restricted to older Python runtimes. Runtime pins include HTTPX 0.28.1,
HTTPcore 1.0.9 and h2 4.4.1. Each identity row hashes actual imported SDK source.

Run only through `ts-heavy-any nice -n 19`, with the outer synthetic tenant helper:
`python benchmark/clients/python/parity.py --credentials PRIVATE.json --out NEW.jsonl`.
The output must be new. Keys, tokens, bodies, source URLs and raw errors are not logged.
`TS_PEERS_FILE` supplies serving regions; it must include gru/sjc/fra. Setup owns one all-regions database and two documents.
Setup has a shared 60 s deadline. Cleanup directly deletes the exact owned name
with a separate 30 s deadline. Cleanup records `logical_accepted`, `absent` or `failed`;
an uncertain create followed by 404, or a failed delete, requires reconciliation.
Remote erasure is unverified. Account/inbox erasure belongs to the outer helper.

Each region runs three alternating repeats of 256/4096 B, c=1/16, raw-h1/raw-h2/sdk.
SDK data/auth pools use the shipped HTTP/2 with HTTP/1.1 fallback, 30 s timeout,
normal TLS, proxy and pool limits. Raw controls force their protocol and use a
separate matched auth pool; the SDK retains its built-in refresh/retry path.
Only exact public setup/document routes receive a region pin. Every warmup/measured
attempt must return 200 and one matching region header, and total attempts must
equal logical operation starts, rejecting hidden retries even after 200 headers.
Document id/version/explicit state/body must match. Readiness is excluded.

Each cell creates a fresh pool/token source, warms for 2 s and measures for 5 s.
Requests starting inside the window retain full latency through a bounded 30 s
drain. Throughput uses the window extended to the last measured completion. A uniform 16,384-sample
reservoir bounds retained latencies. `ok` counts all measured successes; `sampled`
counts retained timings. Connection attempts cover readiness too, via HTTPcore's
`connection.connect_tcp.started` event. Empty, failed, stuck or mismatched cells
are emitted before rejection. Setup/cleanup errors report only status/type.
SIGINT/SIGTERM cancel once and unwind cleanup; repeated signals do not cancel it
again. `transport_control.py` exercises the real SDK over synthetic HTTPX responses;
`window_control.py` checks tails, peers and repeat signals.
`lifecycle_control.py` checks exact-name cleanup, bounded uncertain create and
reconciliation reporting through the real SDK. Live qualification remains.
