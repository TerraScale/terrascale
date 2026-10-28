# Python parity observation controls

`observer.py` observes HTTPX request/response hooks without changing retry, TLS,
proxy or pool policy. Each cell compares observed attempts with logical operation
starts and successful headers. A successful response followed by a retried body
read must fail this comparison even if both attempts have HTTP 200 headers.

Use SDK source `49ea36ee9dfc005b1126e4baa68648b0e8fbd221` and its hash-locked
runtime dependencies. Run through `ts-heavy-any nice -n 19`:
`PYTHONPATH=<SDK>/src python benchmark/clients/python/transport_control.py`.
The control uses the real SDK with HTTPX MockTransport and synthetic credentials;
it contacts no service. It exercises normal reads, wrong/duplicate region headers,
protocol mismatch, retry, 401 refresh, truncated-body retry, and auth cancellation.
Protocol metadata is fabricated: these controls do not qualify ALPN, sockets,
connection reuse, TLS, production setup/deletion or performance.

`runtime.py` provides the bounded measurement window and signal handling used by
`parity.py`. `window_control.py` retains a slow operation crossing the window end,
rejects malformed peers, deduplicates regions, and sends SIGINT followed by SIGTERM
during cleanup to check that cancellation is issued only once. It needs no SDK.
