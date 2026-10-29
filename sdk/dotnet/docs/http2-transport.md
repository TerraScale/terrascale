# HTTPS connection reuse

TerraBase and ts-auth HTTPS requests negotiate HTTP/2 and fall back to HTTP/1.1
when the server does not offer HTTP/2. Requests share the caller's long-lived
`HttpClient` connection pool. This applies to explicitly constructed SDK requests,
independently of `HttpClient.DefaultRequestVersion`.

Private anonymous cleartext requests retain HTTP/2 prior knowledge. Authenticated
cleartext requests retain HTTP/1.1. The SDK preserves caller ownership, TLS
validation, credential handling, request deadlines and existing retry rules.

Negotiation and reuse are covered against isolated TLS HTTP fakes. Performance
evidence belongs in the TerraScale monorepo under `benchmark/bench/`.

The [three-pair transport control](https://github.com/TerraScale/terrascale/blob/fix/api-request-overhead/benchmark/bench/2026-10-27-perf-api-https.md)
measured one HTTP/2 connection rather than 16 HTTP/1.1 connections at c=16 on its recorded SDK sources.
Host contention and inconsistent latency exclude a general speedup claim. Regional document proof follows separately.
