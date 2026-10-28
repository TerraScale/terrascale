# Explicit raw .NET request versions, 2026-10-28

The older `Bench.SendAsync` built explicit `HttpRequestMessage` objects without
setting their version or policy. An offline handler probe confirms that
`HttpClient.DefaultRequestVersion` and `DefaultVersionPolicy` do not apply to
these messages. The raw-h2 label therefore did not establish an HTTP/2 request.
Earlier results need an independent protocol observation before being treated
as HTTP/2 evidence.

| Client defaults | Explicit message | Observed at handler | Policy |
|---|---|---|---|
| HTTP/1.1, exact | Original construction | HTTP/1.1 | RequestVersionOrLower |
| HTTP/1.1, exact | Copy version and policy | HTTP/1.1 | RequestVersionExact |
| HTTP/2, exact | Original construction | HTTP/1.1 | RequestVersionOrLower |
| HTTP/2, exact | Copy version and policy | HTTP/2 | RequestVersionExact |

Code commit `cf63e639e`, based on main `6931b0b57`, copies both settings onto
every raw request. Bodies, tokens, accounting and SDK requests are unchanged.
The separate parity runner already sets explicit versions; this correction
does not change that runner.

| Delta validation at code commit | Result |
|---|---|
| Existing `make check` through `ts-heavy-any nice -n 19` | Passed |
| SDK preparer fixtures | 14 passed |
| Locked restore, Release build, all three format checks | Passed; 0 warnings/errors |
| Parallel benchmark tests | 132 passed, 0 failed/skipped |
| Offline request configuration probe | 4/4 passed |

SDK pin: `36d114877a69d18fba33b27ad362bd3e68e9e7ff` (v0.0.18).
The build used .NET SDK `11.0.100-rc.1.26425.128`. The executed inline job is
preserved as `benchmark/scripts/api-dotnet-raw-version-control-20261028.sh`,
with only its working-directory setup changed to resolve from the script.
The probe creates a temporary dependency-free console project and uses a fake
handler: it opens no sockets and reads no credentials. The preceding checks
fetch the official SDK and restore dependencies.

Log on delta: `/tmp/perf-api-dotnet-raw-request-version-check.log`.
SHA-256: `63aa86844d4db746675800517a419670e81ce9305e33cedd1d226490a29e9aad`.

No live TLS/ALPN or negotiated-protocol observation, tenant, latency result or
speedup is supplied here. Required CI has not run for this branch. The original
server-over-engine <10% target remains unmet and its denominator is unchanged.
