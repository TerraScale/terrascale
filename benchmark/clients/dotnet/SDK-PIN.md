# .NET benchmark SDK pin, 2026-10-28

The benchmark pins ts-dotnet v0.0.18 at
[`36d114877a69d18fba33b27ad362bd3e68e9e7ff`](https://github.com/TerraScale/ts-dotnet/commit/36d114877a69d18fba33b27ad362bd3e68e9e7ff),
the HTTP/2 release measured by the API-overhead worker. Preparation uses the
official GitHub HTTPS origin, checks the exact revision against official main,
refuses a dirty checkout and holds the existing SDK lock during compilation.
The benchmark target and launcher use .NET 11, pinned by `global.json`.

The SDK exposes project database create/delete and index APIs. The API benchmark
creates its owned database with project `POST` and `allRegions: true`; its old
legacy `PUT` route is refused for project tokens. This placement change requires
a new baseline: old and new setup/placement results are not comparable. A
successful DELETE accepts logical deletion; remote erasure is not verified.

The separate workload preflight's `SDK_BLOCKED` result reflects its unimplemented
lifecycle and measurement contract. It is not evidence that the current SDK
lacks these APIs. That preflight remains closed. The API benchmark is still not
the qualified authenticated regional parity runner: exact region/protocol,
attempt, window/drain and recovery controls remain necessary.

The shipped SDK retry policy is preserved. Tokens refresh through the benchmark's
provider; the data client does not automatically retry 401 responses. No live
run, SDK parity result, latency gain or server-over-engine target is established
by this dependency and setup update.
