# Authenticated regional API/SDK control, 2026-10-27

All 108 cells completed: 3 repeats × 3 regions × 2 document sizes × c=1/16 × raw h1/raw h2/SDK.
58,752 timed reads returned this run's exact document id/version and the requested serving-region header.
This is a candidate-SDK versus raw control, not a before/after SDK speedup or server/engine attribution.
The host was not reserved; concurrent builds occurred. These results do not qualify quiet-host latency.

Host beta, 12 vCPU, .NET 11.0.0-rc.1.26425.128; E2E server GC, tiered PGO disabled. Harness `1751aabb8`,
tenant helper `e93264d7d`, SDK `d622039`. Normal TLS validation and public region discovery; one synthetic
allRegions database with home sjc and 256 B/4 KiB JSON. Every measured request verifies its region pin.
Each region/repeat reuses one pooled HttpClient across lanes, excludes a warm wave and then measures 64 reads/worker.
Lane order reverses on repeat two. Only 64 c=1 samples/repeat: p99 cannot qualify tail latency.
The benchmark's single-flight token provider renews at 90% of send-time grant lifetime: 26 exchanges total.
Synthetic Enterprise plan was assigned through authenticated NetBird administration; auth/revocation stayed active.
One test passed, zero failures/skips, duration 22m34.839s. Database deletion was accepted; account receipt reached
204 and inbox deletion settled. The private credentials directory was removed. No physical-erasure claim.

Internal authenticated health probes found revision `9731313c08ed48823823f7355e58dcc22ba8142c` in all regions:
gru `ts-gru-2`, sjc `ts-sjc-1`, fra `ts-fra-1`, service version 0.1.0. This predates the candidate server change.

## Median round trips across all three repeats

The c=1 SDK/raw-h2 median ratios are 0.992–1.079. c=16 varies widely; sjc 4 KiB is 1.255.
Uniform SDK parity is not established. No repeat was discarded.

| Region | Bytes | c | Raw h1 p50 ms | Raw h2 p50 ms | SDK p50 ms | SDK/raw h2 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| gru | 256 | 1 | 21.35 | 21.63 | 21.78 | 1.007 |
| gru | 256 | 16 | 26.62 | 52.72 | 29.68 | 0.563 |
| gru | 4096 | 1 | 22.01 | 21.67 | 23.38 | 1.079 |
| gru | 4096 | 16 | 26.19 | 61.45 | 59.92 | 0.975 |
| sjc | 256 | 1 | 200.36 | 199.84 | 199.83 | 1.000 |
| sjc | 256 | 16 | 213.98 | 256.81 | 224.30 | 0.873 |
| sjc | 4096 | 1 | 199.79 | 199.26 | 202.37 | 1.016 |
| sjc | 4096 | 16 | 229.10 | 217.04 | 272.42 | 1.255 |
| fra | 256 | 1 | 213.15 | 212.60 | 213.06 | 1.002 |
| fra | 256 | 16 | 244.00 | 240.50 | 251.13 | 1.044 |
| fra | 4096 | 1 | 211.06 | 213.08 | 211.29 | 0.992 |
| fra | 4096 | 16 | 234.44 | 264.33 | 258.43 | 0.978 |

## All measured cells

| Region | Repeat | Bytes | c | Lane | Protocol | p50 ms | p99 ms | ops/s |
| --- | ---: | ---: | ---: | --- | --- | ---: | ---: | ---: |
| fra | 1 | 256 | 1 | raw-h1 | 1.1 | 207.32 | 538.89 | 3.9 |
| fra | 1 | 256 | 1 | raw-h2 | 2.0 | 206.80 | 319.02 | 4.5 |
| fra | 1 | 256 | 1 | sdk | 2.0 | 206.01 | 299.88 | 4.5 |
| fra | 1 | 256 | 16 | raw-h1 | 1.1 | 244.00 | 1110.72 | 46.1 |
| fra | 1 | 256 | 16 | raw-h2 | 2.0 | 267.41 | 807.44 | 51.5 |
| fra | 1 | 256 | 16 | sdk | 2.0 | 261.81 | 806.86 | 50.2 |
| fra | 1 | 4096 | 1 | raw-h1 | 1.1 | 204.02 | 273.06 | 4.7 |
| fra | 1 | 4096 | 1 | raw-h2 | 2.0 | 207.04 | 982.24 | 3.9 |
| fra | 1 | 4096 | 1 | sdk | 2.0 | 205.45 | 287.88 | 4.6 |
| fra | 1 | 4096 | 16 | raw-h1 | 1.1 | 255.63 | 681.22 | 56.4 |
| fra | 1 | 4096 | 16 | raw-h2 | 2.0 | 251.66 | 2240.00 | 51.4 |
| fra | 1 | 4096 | 16 | sdk | 2.0 | 258.43 | 1026.30 | 55.0 |
| gru | 1 | 256 | 1 | raw-h1 | 1.1 | 21.24 | 78.83 | 29.9 |
| gru | 1 | 256 | 1 | raw-h2 | 2.0 | 21.63 | 79.78 | 27.8 |
| gru | 1 | 256 | 1 | sdk | 2.0 | 21.78 | 81.01 | 25.6 |
| gru | 1 | 256 | 16 | raw-h1 | 1.1 | 26.62 | 97.24 | 345.9 |
| gru | 1 | 256 | 16 | raw-h2 | 2.0 | 52.72 | 103.68 | 312.1 |
| gru | 1 | 256 | 16 | sdk | 2.0 | 29.68 | 82.94 | 353.9 |
| gru | 1 | 4096 | 1 | raw-h1 | 1.1 | 22.01 | 81.71 | 25.3 |
| gru | 1 | 4096 | 1 | raw-h2 | 2.0 | 21.67 | 80.86 | 27.6 |
| gru | 1 | 4096 | 1 | sdk | 2.0 | 23.38 | 81.92 | 21.9 |
| gru | 1 | 4096 | 16 | raw-h1 | 1.1 | 20.96 | 77.35 | 498.3 |
| gru | 1 | 4096 | 16 | raw-h2 | 2.0 | 67.84 | 176.13 | 276.8 |
| gru | 1 | 4096 | 16 | sdk | 2.0 | 53.10 | 97.97 | 309.0 |
| sjc | 1 | 256 | 1 | raw-h1 | 1.1 | 199.96 | 513.60 | 4.6 |
| sjc | 1 | 256 | 1 | raw-h2 | 2.0 | 199.84 | 476.73 | 4.7 |
| sjc | 1 | 256 | 1 | sdk | 2.0 | 199.80 | 212.47 | 5.0 |
| sjc | 1 | 256 | 16 | raw-h1 | 1.1 | 213.98 | 625.90 | 56.5 |
| sjc | 1 | 256 | 16 | raw-h2 | 2.0 | 239.37 | 1173.57 | 46.7 |
| sjc | 1 | 256 | 16 | sdk | 2.0 | 212.06 | 596.18 | 66.2 |
| sjc | 1 | 4096 | 1 | raw-h1 | 1.1 | 199.51 | 222.60 | 5.0 |
| sjc | 1 | 4096 | 1 | raw-h2 | 2.0 | 199.26 | 673.89 | 4.4 |
| sjc | 1 | 4096 | 1 | sdk | 2.0 | 200.38 | 421.77 | 4.7 |
| sjc | 1 | 4096 | 16 | raw-h1 | 1.1 | 223.39 | 545.17 | 65.8 |
| sjc | 1 | 4096 | 16 | raw-h2 | 2.0 | 217.04 | 591.83 | 65.5 |
| sjc | 1 | 4096 | 16 | sdk | 2.0 | 292.57 | 1329.85 | 42.6 |
| fra | 2 | 256 | 1 | sdk | 2.0 | 216.06 | 422.61 | 4.1 |
| fra | 2 | 256 | 1 | raw-h2 | 2.0 | 214.90 | 319.84 | 4.3 |
| fra | 2 | 256 | 1 | raw-h1 | 1.1 | 218.28 | 701.91 | 3.5 |
| fra | 2 | 256 | 16 | sdk | 2.0 | 251.13 | 609.71 | 59.5 |
| fra | 2 | 256 | 16 | raw-h2 | 2.0 | 240.50 | 541.46 | 60.5 |
| fra | 2 | 256 | 16 | raw-h1 | 1.1 | 241.80 | 2709.17 | 50.9 |
| fra | 2 | 4096 | 1 | sdk | 2.0 | 211.29 | 578.35 | 4.0 |
| fra | 2 | 4096 | 1 | raw-h2 | 2.0 | 213.08 | 497.84 | 4.2 |
| fra | 2 | 4096 | 1 | raw-h1 | 1.1 | 213.34 | 319.46 | 4.3 |
| fra | 2 | 4096 | 16 | sdk | 2.0 | 268.07 | 2256.50 | 46.7 |
| fra | 2 | 4096 | 16 | raw-h2 | 2.0 | 285.49 | 1108.62 | 50.0 |
| fra | 2 | 4096 | 16 | raw-h1 | 1.1 | 234.44 | 599.68 | 60.5 |
| gru | 2 | 256 | 1 | sdk | 2.0 | 22.30 | 84.39 | 22.9 |
| gru | 2 | 256 | 1 | raw-h2 | 2.0 | 22.47 | 117.28 | 17.8 |
| gru | 2 | 256 | 1 | raw-h1 | 1.1 | 68.11 | 98.54 | 19.3 |
| gru | 2 | 256 | 16 | sdk | 2.0 | 81.57 | 192.56 | 222.7 |
| gru | 2 | 256 | 16 | raw-h2 | 2.0 | 73.42 | 271.85 | 238.3 |
| gru | 2 | 256 | 16 | raw-h1 | 1.1 | 72.05 | 126.15 | 283.0 |
| gru | 2 | 4096 | 1 | sdk | 2.0 | 23.81 | 126.19 | 21.4 |
| gru | 2 | 4096 | 1 | raw-h2 | 2.0 | 23.73 | 102.44 | 20.7 |
| gru | 2 | 4096 | 1 | raw-h1 | 1.1 | 23.33 | 88.47 | 22.9 |
| gru | 2 | 4096 | 16 | sdk | 2.0 | 59.92 | 260.94 | 270.9 |
| gru | 2 | 4096 | 16 | raw-h2 | 2.0 | 61.45 | 110.83 | 272.6 |
| gru | 2 | 4096 | 16 | raw-h1 | 1.1 | 60.82 | 106.67 | 286.4 |
| sjc | 2 | 256 | 1 | sdk | 2.0 | 199.83 | 251.96 | 4.9 |
| sjc | 2 | 256 | 1 | raw-h2 | 2.0 | 200.75 | 426.06 | 4.5 |
| sjc | 2 | 256 | 1 | raw-h1 | 1.1 | 207.36 | 967.12 | 3.2 |
| sjc | 2 | 256 | 16 | sdk | 2.0 | 239.21 | 401.27 | 62.4 |
| sjc | 2 | 256 | 16 | raw-h2 | 2.0 | 272.83 | 778.41 | 48.4 |
| sjc | 2 | 256 | 16 | raw-h1 | 1.1 | 273.37 | 1006.90 | 48.9 |
| sjc | 2 | 4096 | 1 | sdk | 2.0 | 204.94 | 292.63 | 4.7 |
| sjc | 2 | 4096 | 1 | raw-h2 | 2.0 | 199.31 | 219.48 | 5.0 |
| sjc | 2 | 4096 | 1 | raw-h1 | 1.1 | 200.41 | 1252.58 | 3.9 |
| sjc | 2 | 4096 | 16 | sdk | 2.0 | 226.17 | 1025.17 | 61.8 |
| sjc | 2 | 4096 | 16 | raw-h2 | 2.0 | 207.94 | 584.91 | 67.6 |
| sjc | 2 | 4096 | 16 | raw-h1 | 1.1 | 229.10 | 1636.99 | 53.2 |
| fra | 3 | 256 | 1 | raw-h1 | 1.1 | 213.15 | 435.17 | 4.1 |
| fra | 3 | 256 | 1 | raw-h2 | 2.0 | 212.60 | 533.93 | 4.2 |
| fra | 3 | 256 | 1 | sdk | 2.0 | 213.06 | 270.59 | 4.5 |
| fra | 3 | 256 | 16 | raw-h1 | 1.1 | 252.86 | 2235.29 | 47.6 |
| fra | 3 | 256 | 16 | raw-h2 | 2.0 | 224.70 | 598.96 | 64.4 |
| fra | 3 | 256 | 16 | sdk | 2.0 | 240.78 | 691.26 | 60.2 |
| fra | 3 | 4096 | 1 | raw-h1 | 1.1 | 211.06 | 673.89 | 3.8 |
| fra | 3 | 4096 | 1 | raw-h2 | 2.0 | 215.05 | 404.49 | 4.0 |
| fra | 3 | 4096 | 1 | sdk | 2.0 | 212.38 | 406.61 | 4.3 |
| fra | 3 | 4096 | 16 | raw-h1 | 1.1 | 231.31 | 795.81 | 57.6 |
| fra | 3 | 4096 | 16 | raw-h2 | 2.0 | 264.33 | 1291.92 | 50.5 |
| fra | 3 | 4096 | 16 | sdk | 2.0 | 223.43 | 660.94 | 62.5 |
| gru | 3 | 256 | 1 | raw-h1 | 1.1 | 21.35 | 81.27 | 27.8 |
| gru | 3 | 256 | 1 | raw-h2 | 2.0 | 20.57 | 84.66 | 25.4 |
| gru | 3 | 256 | 1 | sdk | 2.0 | 19.87 | 87.87 | 26.4 |
| gru | 3 | 256 | 16 | raw-h1 | 1.1 | 25.33 | 98.49 | 353.9 |
| gru | 3 | 256 | 16 | raw-h2 | 2.0 | 24.72 | 82.39 | 391.2 |
| gru | 3 | 256 | 16 | sdk | 2.0 | 25.06 | 81.66 | 364.0 |
| gru | 3 | 4096 | 1 | raw-h1 | 1.1 | 21.99 | 79.90 | 25.6 |
| gru | 3 | 4096 | 1 | raw-h2 | 2.0 | 20.58 | 102.63 | 22.7 |
| gru | 3 | 4096 | 1 | sdk | 2.0 | 19.31 | 81.24 | 33.3 |
| gru | 3 | 4096 | 16 | raw-h1 | 1.1 | 26.19 | 114.11 | 317.6 |
| gru | 3 | 4096 | 16 | raw-h2 | 2.0 | 60.51 | 109.18 | 275.7 |
| gru | 3 | 4096 | 16 | sdk | 2.0 | 78.29 | 112.81 | 230.8 |
| sjc | 3 | 256 | 1 | raw-h1 | 1.1 | 200.36 | 289.36 | 4.8 |
| sjc | 3 | 256 | 1 | raw-h2 | 2.0 | 199.83 | 471.00 | 4.8 |
| sjc | 3 | 256 | 1 | sdk | 2.0 | 200.03 | 223.20 | 4.9 |
| sjc | 3 | 256 | 16 | raw-h1 | 1.1 | 203.12 | 532.99 | 67.7 |
| sjc | 3 | 256 | 16 | raw-h2 | 2.0 | 256.81 | 644.46 | 54.8 |
| sjc | 3 | 256 | 16 | sdk | 2.0 | 224.30 | 540.57 | 64.6 |
| sjc | 3 | 4096 | 1 | raw-h1 | 1.1 | 199.79 | 259.15 | 4.9 |
| sjc | 3 | 4096 | 1 | raw-h2 | 2.0 | 197.85 | 399.12 | 4.6 |
| sjc | 3 | 4096 | 1 | sdk | 2.0 | 202.37 | 691.92 | 4.1 |
| sjc | 3 | 4096 | 16 | raw-h1 | 1.1 | 255.45 | 540.85 | 57.8 |
| sjc | 3 | 4096 | 16 | raw-h2 | 2.0 | 226.98 | 542.93 | 63.4 |
| sjc | 3 | 4096 | 16 | sdk | 2.0 | 272.42 | 1009.34 | 41.6 |
