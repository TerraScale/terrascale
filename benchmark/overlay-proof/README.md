# Local shape overlay live proof

Run `run.py` on `ts-sjc-1` with a private stdin JSON object containing `apiKey`
and `project`, using a unique Free account from `ts-throwaway-account`. It uses
Python 3's standard library and the public API through that host's local TLS
edge. The caller owns account creation, credential transfer and account cleanup.

The runner creates a disposable gru database with an sjc replica, follows shape
snapshot redirects, qualifies the sjc live tail, performs five warmup writes and
three measured repeats of 20 local writes, then overlaps a home and local insert
to force a home rejection. It requires matching provisional and confirmed/revert
transaction IDs, verifies the home winner, and deletes its database in `finally`.

```sh
python3 run.py < /private/credentials.json > /private/result.json
```

Progress goes to stderr. The JSON result contains synthetic rows, tail events,
read retries, measurements and cleanup state, never credentials. Catch-up,
capacity and transient server errors on replay-safe shape reads receive bounded
retry; writes are never retried. A failed assertion exits nonzero and retains
partial evidence and database cleanup state on stdout.

The companion Go command [`../cmd/overlay-bench`](../cmd/overlay-bench/main.go)
measures forwarded writes over HTTP/2. Cross-build it for linux/arm64 on a dev
host. Use `-region sjc -writes 20 -repeats 4`, reserving the first complete repeat
as the fixed warmup and retaining all samples. See the
[live report](../bench/2026-11-05-f7-live.md) for results and measurement limits.
