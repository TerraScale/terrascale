# .NET SDK / raw HTTPS parity runner: offline qualification (2026-10-28)

Offline controls for the `api-bench parity` runner
([contract](../clients/dotnet/PARITY-RUNNER.md)). No credentials, tenant,
production request or live timing were involved. This is not a performance
result, and live parity remains unqualified.

| Item | Value |
| --- | --- |
| Host | delta, `TS_HEAVY_HOST=delta ts-heavy-any nice -n 19 … make -C benchmark/clients/dotnet check` |
| SDK | ts-dotnet `v0.0.18`, `36d114877a69d18fba33b27ad362bd3e68e9e7ff`, official GitHub clone |
| .NET SDK | `11.0.100-rc.1.26425.128` (`global.json`) |
| Base | `400e0fee0` (documents foundation) |
| `ParityCell.cs` SHA-256 | `508673d1eb3c6376bad51b11510b4b1b5e68d20412e7e3037fd0af89700291c8` |
| `ParityRun.cs` SHA-256 | `6d86f1b71f672743ab4502edc82c9fcd42e2c23bfeb0c348047ad663eda00032` |
| `Program.cs` SHA-256 | `a5bbf06d20e5bba1cdcbe04ab9e220a141e6b1ae2b6ffdb2032e0faf6a8bad29` |
| `Tokens.cs` SHA-256 | `1d44ec679fb53fb1bd9d16a2d6497d09f46d6936a8e5413d09b4bd13e0c4f8ad` |
| `ParityAttempts.cs` SHA-256 | `04357754e2230b416b07e9403e2e875d42b59160391c53fe321c1e26d473a3dd` |

`make check` runs the SDK preparation checks, locked restore, Release build
(warnings as errors), `dotnet format --verify-no-changes` for all three projects
and every test.

| Layer | Branch | Head | Started (UTC) | Tests | Exit | Log (delta) | Log SHA-256 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| cells | `fix/api-dotnet-parity-cells` | `e4a33fc10` | 09:40:41 | 170/170 | 0 | `~/parity-runner-logs/cells-e4a33fc10.log` | `aa4de7acc0c1bc3074f0dff900ffce0841aac8a30f7524c7d56826e5105d9e5d` |
| runner | `fix/api-dotnet-parity-runner` | `b19d97ed1` | 09:46:21 | 180/180 | 0 | `~/parity-runner-logs/runner-b19d97ed1.log` | `0000db564df6c607062a794454b431799f439ee0c49a5fca3ae256036d2cf253` |
| lifecycle | `fix/api-dotnet-parity-lifecycle` | `68c07bba7` | 09:47:40 | 186/186 | 0 | `~/parity-runner-logs/lifecycle-68c07bba7.log` | `571820c562dca779e2f45ef85b4360293092dbe38b2a3fa7dd9f9cba9c57760b` |

Earlier attempts on the same branches failed and were fixed before these runs.
They hit a test-only raw-string compile error, a readiness assertion, fake SDK
JSON shapes, an output-sharing lock that blocked a test read and an ambiguous
`Program` reference. This report adds only itself on top of `68c07bba7`; the code
and test trees are unchanged.

| Control | Result |
| --- | --- |
| raw-h1 / raw-h2 send `RequestVersionExact` 1.1 / 2.0; SDK sends 2.0 `OrLower` | pass |
| retry (SDK 503), body, 401, 500, wrong region, raw-h2 answered as h1 reject the cell; SDK h1 reported, not rejected | pass |
| readiness kept out of the window snapshot; bounded readiness rejects with its last reason | pass |
| token exchange inside a cell rejects it; prewarmed token keeps auth out; pinned auth request rejects | pass |
| counted `ConnectCallback`: SDK handler options kept, keep-alive gives 1 connect, refused connect not counted | pass |
| full 3-region matrix (108 cells): home `fra` pin on every setup/cleanup request, lane order alternates, owned row on disk before create, output 0600 | pass |
| first rejected cell is emitted, then the run stops and cleans up | pass |
| bad/unknown/missing credential members, bad key, group-readable or symlinked credentials, existing or symlinked output, missing region: exit 2, no network, no echo | pass |
| uncertain create + 404 → `reconcile_required`; refused + 404 → not; delete 500 → `failed` | pass |
| setup deadline covers the collection step; cleanup keeps its own deadline | pass |
| repeated SIGINT/SIGTERM handled during an uncertain create and its cleanup | pass |
| interrupt inside a cell emits its `interrupted` evidence before cleanup | pass |

Fake handlers do not negotiate TLS or ALPN. Signals are delivered by calling
the handler with constructed `PosixSignalContext`s. Wire replays inside
`SocketsHttpHandler` stay `null`.
