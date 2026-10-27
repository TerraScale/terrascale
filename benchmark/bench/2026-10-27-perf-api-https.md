# API SDK HTTPS transport control, 2026-10-27

The candidate negotiated HTTP/2 and multiplexed c=16 onto one TCP connection in all three repeats;
the baseline used HTTP/1.1 and 16 connections. **No consistent latency improvement was measured.**
Host contention means these are diagnostic results, not the required quiet-host speedup proof.
This public JWKS control does not establish authenticated document latency, revocation latency,
server/engine attribution, or live coverage of gru/sjc/fra. The server overhead <10% target remains unproven.

## Method

- Host beta (12 vCPU), .NET 11.0.0-rc.1.26425.128, E2E server GC and tiered PGO disabled, normal TLS validation.
- Harness `5be61b714`; SDK before `e7542eaa25b2e9c1423b12d731e9edcd47aceaf5`, after `1d91a88a0080e98f2a51698b62b08d9e2a8afcbb`.
  `run-https.sh` verifies the complete baseline SDK `src` tree before building; only HTTPS request version selection differs.
- Public `https://auth.terrascale.tech/terrascale/.well-known/jwks.json`; no tenant or credentials, no cleanup needed.
  Edge routing is not pinned to a region. Raw h1/h2 and SDK all deserialize and validate a nonempty JWK set.
- Three same-host pairs, alternating before/after, after/before, before/after. Lane order reverses on repeat two.
  Each cell owns a long-lived pooled HttpClient: one cold wave followed by 64 sequential requests per worker.
  Cold-wave time and connection count include setup; p50/p99 cover only the subsequent 64*c requests.
  The c=1 p99 has only 64 samples per repeat and cannot qualify tail latency.
- Both builds and all six arms passed with zero skipped or failed tests. SDK TLS negotiation/fallback/reuse integration
  tests also passed all four cases on .NET 11. No auth, metering, storage, or revocation implementation changed.
- Executed through `TS_HEAVY_HOST=beta ts-heavy-any nice -n 19 bash benchmark/dotnet/VsTurso/run-https.sh
  <clean-sdk-checkout> e7542eaa25b2e9c1423b12d731e9edcd47aceaf5 benchmark/bench/2026-10-27-perf-api-https
  --repo terrascale --branch fix/api-request-overhead`. Builds finish before timed arms begin.

## Host evidence

The machine was not quiet: load1 ranged 4.30–8.48 and CPU pressure avg10 reached 17.91%.
No outlier or arm was discarded. CPU pressure is host-wide, not process CPU time.

| Arm | Repeat | UTC start–end | load1 start–end | CPU some avg10 % start–end | CPU some delta µs |
| --- | ---: | --- | --- | --- | ---: |
| before | 1 | 2026-10-27T23:15:46Z–2026-10-27T23:15:56Z | 8.48–7.33 | 9.40–3.46 | 41346 |
| after | 1 | 2026-10-27T23:15:56Z–2026-10-27T23:16:06Z | 7.33–6.20 | 3.46–4.06 | 499101 |
| after | 2 | 2026-10-27T23:16:06Z–2026-10-27T23:16:15Z | 6.20–5.32 | 4.06–1.49 | 29397 |
| before | 2 | 2026-10-27T23:16:15Z–2026-10-27T23:16:25Z | 5.32–4.51 | 1.49–0.67 | 28273 |
| before | 3 | 2026-10-27T23:16:25Z–2026-10-27T23:16:35Z | 4.51–4.30 | 0.67–0.60 | 142935 |
| after | 3 | 2026-10-27T23:16:35Z–2026-10-27T23:16:45Z | 4.30–7.42 | 0.60–17.91 | 3032022 |

## All measured cells

| Arm | Repeat | c | Lane | Protocol | Connections | Cold wave ms | p50 ms | p99 ms | ops/s |
| --- | ---: | ---: | --- | --- | ---: | ---: | ---: | ---: | ---: |
| before | 1 | 1 | raw-h1 | 1.1 | 1 | 137.44 | 20.73 | 24.01 | 47.8 |
| before | 1 | 1 | raw-h2 | 2.0 | 1 | 71.59 | 21.17 | 23.19 | 46.9 |
| before | 1 | 1 | sdk | 1.1 | 1 | 78.62 | 21.38 | 22.78 | 46.8 |
| before | 1 | 16 | raw-h1 | 1.1 | 16 | 75.21 | 20.41 | 39.67 | 693.5 |
| before | 1 | 16 | raw-h2 | 2.0 | 1 | 71.59 | 20.34 | 38.32 | 714.2 |
| before | 1 | 16 | sdk | 1.1 | 16 | 69.83 | 21.32 | 67.80 | 655.9 |
| after | 1 | 1 | raw-h1 | 1.1 | 1 | 135.63 | 22.00 | 24.57 | 45.4 |
| after | 1 | 1 | raw-h2 | 2.0 | 1 | 68.13 | 19.23 | 21.88 | 51.5 |
| after | 1 | 1 | sdk | 2.0 | 1 | 80.96 | 21.12 | 23.09 | 47.0 |
| after | 1 | 16 | raw-h1 | 1.1 | 16 | 71.56 | 21.05 | 38.21 | 661.5 |
| after | 1 | 16 | raw-h2 | 2.0 | 1 | 85.75 | 21.93 | 31.32 | 683.7 |
| after | 1 | 16 | sdk | 2.0 | 1 | 80.94 | 22.39 | 34.57 | 676.1 |
| after | 2 | 1 | sdk | 2.0 | 1 | 143.55 | 20.07 | 21.51 | 50.0 |
| after | 2 | 1 | raw-h2 | 2.0 | 1 | 75.04 | 21.78 | 25.00 | 45.3 |
| after | 2 | 1 | raw-h1 | 1.1 | 1 | 64.48 | 19.39 | 21.03 | 51.1 |
| after | 2 | 16 | sdk | 2.0 | 1 | 89.03 | 22.02 | 66.31 | 660.1 |
| after | 2 | 16 | raw-h2 | 2.0 | 1 | 75.64 | 20.13 | 34.97 | 741.7 |
| after | 2 | 16 | raw-h1 | 1.1 | 16 | 77.89 | 20.59 | 36.04 | 700.5 |
| before | 2 | 1 | sdk | 1.1 | 1 | 141.06 | 21.46 | 24.60 | 46.4 |
| before | 2 | 1 | raw-h2 | 2.0 | 1 | 82.68 | 22.17 | 26.04 | 44.5 |
| before | 2 | 1 | raw-h1 | 1.1 | 1 | 68.43 | 21.17 | 22.66 | 47.3 |
| before | 2 | 16 | sdk | 1.1 | 16 | 73.91 | 20.64 | 32.03 | 713.5 |
| before | 2 | 16 | raw-h2 | 2.0 | 1 | 73.13 | 19.31 | 31.92 | 760.2 |
| before | 2 | 16 | raw-h1 | 1.1 | 16 | 66.94 | 20.59 | 32.87 | 697.0 |
| before | 3 | 1 | raw-h1 | 1.1 | 1 | 139.75 | 21.43 | 25.12 | 46.4 |
| before | 3 | 1 | raw-h2 | 2.0 | 1 | 73.98 | 21.51 | 23.83 | 46.1 |
| before | 3 | 1 | sdk | 1.1 | 1 | 79.59 | 21.64 | 24.83 | 45.8 |
| before | 3 | 16 | raw-h1 | 1.1 | 16 | 69.59 | 21.05 | 44.19 | 654.9 |
| before | 3 | 16 | raw-h2 | 2.0 | 1 | 80.07 | 20.98 | 34.02 | 718.4 |
| before | 3 | 16 | sdk | 1.1 | 16 | 69.60 | 20.76 | 34.19 | 714.3 |
| after | 3 | 1 | raw-h1 | 1.1 | 1 | 187.31 | 19.09 | 21.68 | 51.9 |
| after | 3 | 1 | raw-h2 | 2.0 | 1 | 73.65 | 22.90 | 32.04 | 42.6 |
| after | 3 | 1 | sdk | 2.0 | 1 | 197.59 | 21.55 | 32.25 | 44.5 |
| after | 3 | 16 | raw-h1 | 1.1 | 16 | 154.63 | 21.51 | 33.82 | 670.7 |
| after | 3 | 16 | raw-h2 | 2.0 | 1 | 99.73 | 21.32 | 31.08 | 702.5 |
| after | 3 | 16 | sdk | 2.0 | 1 | 83.22 | 20.21 | 38.98 | 724.7 |
