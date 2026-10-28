# .NET parity observation controls, 2026-10-28

Offline source: `ab10a33d2`, stacked on the SDK compatibility update
`a87d5fd82`. SDK: official ts-dotnet v0.0.18,
`36d114877a69d18fba33b27ad362bd3e68e9e7ff`. No production request,
credential, tenant or performance measurement was used.

| Check on beta | Result |
|---|---|
| Existing SDK-preparer fixtures | 14 passed |
| Locked restore, three benchmark projects and SDK | Passed; locks unchanged |
| Release build | Passed; 0 warnings, 0 errors |
| Existing format checks, three projects | Passed |
| Existing tests plus observation/peers controls | 132 passed, 0 failed, 0 skipped |
| Official pinned SDK checkout after checks | Clean |

Command: `TS_HEAVY_HOST=beta ts-heavy-any nice -n 19 bash
/tmp/perf-api-dotnet-parity-qualification-a.sh --repo terrascale --branch
fix/api-dotnet-parity-core`. The script runs `make check` from
`benchmark/clients/dotnet`, then verifies the SDK checkout and unchanged locks.
Artifacts on beta are under
`~/remote-builds/terrascale/fix-api-dotnet-parity-core-b2e764eca3f9401ffd0df8f91aa9b05dbc089f071458cdf22bd6a32411c9cc56/benchmark/bench/api-dotnet-parity-qualification-a/`.
`check.log` SHA-256:
`5cd33766f3af8681b9e90dc0f1c1b661e5870caf4b5b9d1d909c9cc01860317d`.

The fake-handler controls preserve the shipped SDK's 503 retry and reject its
extra attempt; a 401 is returned without an automatic SDK retry. They also reject
wrong status, target, pin, serving region and protocol. Strict peers parsing
requires gru/sjc/fra and retains every additional discovered region. None of
these controls negotiate TLS or observe internal SocketsHttpHandler wire
replays. This is a foundation, not a qualified full regional parity runner;
there is no speedup or server-over-engine result.
