# Merged .NET SDK HTTPS transport control, 2026-10-28

This public JWKS control compares the merged SDK with its parent. It does not
measure authenticated documents, engine time, revocation, or per-region routing.
No speedup or completion of the server-over-engine <10% target is claimed.

The baseline is `fba075bcba060afb560c711b395385626df91161`; the merged SDK is
`36d114877a69d18fba33b27ad362bd3e68e9e7ff` (ts-dotnet #100). The harness is
`310fae9dd47077aaad903fbb98bc3f170db2a077` (terrascale #779). Beta, .NET 11 RC1,
server GC; normal TLS validation, public auth.terrascale.tech JWKS endpoint.

Three alternating before/after pairs use the existing HttpsTransportBenchmark:
one cold wave, then 64 fully read/deserialized responses per worker at c=1/16.
The source baseline guard checks all SDK src files; its restore list includes the
two version-selection files and the project version metadata. No other SDK
source differs. Matching DLL hashes and complete raw cells are retained.
Only 64 c=1 samples per repeat: p99 does not qualify tail latency.

Builds completed before timing. Remaining heavy-admission slots were held only
during each comparison, without stopping other work. A 1 Hz process monitor
excludes the benchmark process tree. Predeclared quiet criteria: external CPU
mean <=25% and p95 <=100% of one CPU, no compiler/test observed at >=10%.
Every arm and rejected host window is retained; quiet status is assessed below.

An initial build attempt used an SDK checkout nested inside the monorepo and
failed NU1008 by inheriting central package configuration. No timed cell ran.
The successful build uses a separate SDK checkout outside the monorepo.

## Rejected window

56 samples: external CPU mean/p95/max 32.84/107.20/127.12% of one CPU.
Rejected as quiet: compiler work overlapped timing; no quiet-host claim follows.
All six arms passed, zero failures/skips, 36 cells. Triplets are repeats 1, 2, 3.

| Arm | c | Lane | Protocol | Connections | Cold ms | p50 ms | p99 ms | ops/s |
| --- | ---: | --- | --- | --- | --- | --- | --- | --- |
| before | 1 | raw-h1 | 1.1 | 1, 1, 1 | 271.55, 64.98, 128.79 | 21.28, 19.86, 19.81 | 23.86, 23.31, 22.12 | 46.5, 49.5, 50.2 |
| before | 1 | raw-h2 | 2.0 | 1, 1, 1 | 72.99, 75.5, 76.87 | 21.22, 19.79, 21.77 | 24.61, 20.98, 23.59 | 46.6, 50.6, 45.8 |
| before | 1 | sdk | 1.1 | 1, 1, 1 | 72.64, 138.27, 129.26 | 19.13, 19.76, 21.6 | 21.97, 22.43, 22.76 | 51.7, 50.3, 46.2 |
| before | 16 | raw-h1 | 1.1 | 16, 16, 16 | 76.43, 73.36, 76.19 | 20.7, 20.62, 20.91 | 28.95, 27.06, 27.86 | 721.5, 746.9, 726.1 |
| before | 16 | raw-h2 | 2.0 | 1, 1, 1 | 72.95, 85.52, 87.46 | 19.07, 22.52, 21.29 | 29.93, 33.84, 44.0 | 770.2, 672.8, 710.9 |
| before | 16 | sdk | 1.1 | 16, 16, 16 | 72.09, 74.6, 70.34 | 20.27, 20.62, 20.59 | 27.8, 27.84, 32.94 | 756.4, 730.0, 722.4 |
| after | 1 | raw-h1 | 1.1 | 1, 1, 1 | 136.21, 70.11, 138.97 | 21.3, 21.38, 22.06 | 24.01, 22.6, 24.28 | 46.5, 47.1, 45.3 |
| after | 1 | raw-h2 | 2.0 | 1, 1, 1 | 73.97, 70.08, 78.67 | 21.8, 19.21, 22.37 | 24.03, 21.47, 23.81 | 45.4, 52.0, 44.7 |
| after | 1 | sdk | 2.0 | 1, 1, 1 | 70.01, 139.65, 84.5 | 18.6, 19.68, 21.94 | 21.21, 23.53, 23.96 | 53.3, 50.4, 45.3 |
| after | 16 | raw-h1 | 1.1 | 16, 16, 16 | 77.66, 69.32, 73.72 | 20.36, 20.59, 20.77 | 27.34, 27.09, 27.89 | 742.1, 751.5, 728.4 |
| after | 16 | raw-h2 | 2.0 | 1, 1, 1 | 79.78, 81.22, 75.92 | 21.31, 21.13, 19.46 | 35.06, 34.95, 28.39 | 712.9, 706.7, 779.6 |
| after | 16 | sdk | 2.0 | 1, 1, 1 | 83.67, 90.95, 85.08 | 20.95, 21.04, 21.37 | 28.11, 30.68, 30.25 | 730.9, 723.3, 717.6 |

Raw arms, logs, host snapshots and CPU monitor: `perf-api-https-merged` in the artifact directories below.

## Quiet window

56 samples: external CPU mean/p95/max 3.48/7.92/9.90% of one CPU.
Passes the predeclared quiet control.
All six arms passed, zero failures/skips, 36 cells. Triplets are repeats 1, 2, 3.

| Arm | c | Lane | Protocol | Connections | Cold ms | p50 ms | p99 ms | ops/s |
| --- | ---: | --- | --- | --- | --- | --- | --- | --- |
| before | 1 | raw-h1 | 1.1 | 1, 1, 1 | 178.72, 64.5, 131.15 | 20.43, 18.91, 19.79 | 22.98, 20.55, 21.1 | 48.7, 52.5, 50.4 |
| before | 1 | raw-h2 | 2.0 | 1, 1, 1 | 82.25, 74.74, 76.05 | 22.35, 19.52, 22.1 | 23.59, 21.1, 23.82 | 44.8, 51.1, 45.3 |
| before | 1 | sdk | 1.1 | 1, 1, 1 | 77.92, 142.32, 84.02 | 19.42, 22.21, 21.71 | 21.85, 25.52, 25.09 | 51.5, 44.8, 45.7 |
| before | 16 | raw-h1 | 1.1 | 16, 16, 16 | 74.47, 78.83, 79.67 | 20.41, 20.82, 20.84 | 31.77, 33.49, 28.78 | 724.1, 715.3, 732.1 |
| before | 16 | raw-h2 | 2.0 | 1, 1, 1 | 79.2, 83.78, 86.23 | 18.8, 21.57, 21.14 | 24.72, 31.05, 29.91 | 823.7, 698.3, 710.6 |
| before | 16 | sdk | 1.1 | 16, 16, 16 | 72.56, 73.84, 70.98 | 20.5, 20.29, 20.39 | 24.77, 28.3, 26.32 | 755.1, 745.4, 746.6 |
| after | 1 | raw-h1 | 1.1 | 1, 1, 1 | 130.0, 70.35, 134.05 | 19.84, 21.58, 21.5 | 21.78, 23.05, 23.08 | 50.3, 46.2, 46.5 |
| after | 1 | raw-h2 | 2.0 | 1, 1, 1 | 74.95, 69.88, 72.69 | 21.54, 19.55, 20.02 | 23.72, 21.26, 21.41 | 46.2, 51.1, 49.8 |
| after | 1 | sdk | 2.0 | 1, 1, 1 | 84.2, 140.15, 76.6 | 21.89, 19.47, 19.61 | 22.66, 21.51, 20.33 | 45.7, 51.3, 50.9 |
| after | 16 | raw-h1 | 1.1 | 16, 16, 16 | 74.88, 72.34, 75.78 | 20.73, 20.02, 20.79 | 28.54, 25.93, 28.39 | 731.4, 757.0, 710.0 |
| after | 16 | raw-h2 | 2.0 | 1, 1, 1 | 83.55, 87.95, 83.32 | 21.66, 21.66, 21.3 | 28.89, 30.5, 30.43 | 709.2, 697.2, 708.2 |
| after | 16 | sdk | 2.0 | 1, 1, 1 | 81.46, 86.91, 77.23 | 20.94, 21.17, 19.83 | 29.58, 28.12, 27.58 | 724.8, 710.0, 772.8 |

Raw arms, logs, host snapshots and CPU monitor: `perf-api-https-reserved` in the artifact directories below.

## Interpretation

At c=1, SDK p50 medians before/after are 21.71/19.61 ms;
after/raw-h2 median ratio is 0.980. Repeat ranges overlap; no consistent latency gain is established.
At c=16, SDK p50 medians before/after are 20.39/20.94 ms;
after/raw-h2 median ratio is 0.967. Repeat ranges overlap; no consistent latency gain is established.
At c=16 every baseline SDK cell used HTTP/1.1 with 16 connections; every merged SDK cell used
HTTP/2 with one connection. Matched raw-h2 controls also use one connection. This establishes
protocol/connection behavior on the merged source; it does not establish uniform SDK parity,
authenticated regional performance, or the original server-over-engine target.

## Artifacts and reproduction

Host beta retains both windows under
`/home/mariogk/remote-builds/terrascale/fix-api-request-overhead-7da853e0eb5b7bd285a10942b2ab433859daba2c3d0916c0faabaa6edeca75b1/benchmark/bench/`:
`https-merged-0018-quiet-2` (rejected) and `https-merged-0018-reserved` (quiet).
The initial failed build log remains in `https-merged-0018-quiet`.
Local copies are `/tmp/perf-api-https-merged` and `/tmp/perf-api-https-reserved` (temporary analysis copies).

Executed through `TS_HEAVY_HOST=beta ts-heavy-any nice -n 19 ... --repo terrascale --branch fix/api-request-overhead`.
`benchmark/scripts/api-https-merged-20261028.sh` stages the existing runner with the three-file restore list;
`benchmark/scripts/api-https-reserved-20261028.sh` verifies the same DLL hashes and reuses them without rebuilding.
Both scripts retain all host evidence and reserve the remaining three heavy-admission slots only during timing.
Slots are released on exit. The control uses public JWKS, no tenant or credentials; no cleanup was required.

| Artifact | SHA-256 |
| --- | --- |
| before/TerraBase.E2E.dll | `e4fcde0b84b357be3753a9173a57cf5f0860f29c6385f9c7f5f782273c98663d` |
| after/TerraBase.E2E.dll | `5605d898169b56df1e50d21ae37a8ce5f8e4de3e266aff265c0c61261ae3b2f5` |
| before/TerraScale.DotNet.dll | `82037619d1a7249b64008c8d0a5bc7a48e4cdf6147f135133ca89d45fd2a3d77` |
| after/TerraScale.DotNet.dll | `a4f3be7fccbf55ff5b5516cb5d0063982a67771fd47c2d45fe7cb982e641e19a` |
