# Point-GET ETag reuse measurements, 2026-10-27

The candidate reuses the validator already computed for conditional headers in the response body. It removes one
SHA-256 computation and version-string construction from a live point GET. Other body callers compute their own version.
Auth, key revocation and storage behavior are not changed. The Fast round-trip check now compares body version to ETag.

The measurements do not establish a general latency speedup or the <10% server-over-engine target. Fixed-core p50
medians improved nominally, but repeat ranges overlap; medium c=16 p99 increased 6.5%. Allocation was lower in all
fixed-core median cells. Keep the c=1/c=16 and size-specific results instead of claiming one overall percentage.
The original engine denominator is preserved; even hosting-only overhead remains far above 10%.

## Method and validity

Delta, Intel i7-13620H, .NET 11.0.0-rc.1.26425.128, production workstation/concurrent GC and tiered PGO.
Matched artifacts from harness `3a25bdeeb`, server before `0f56119a3`, after `3a25bdeeb`, SDK `d622039` in both arms.
All server src files except GetDocumentEndpoint.cs match the before ref. Both Release builds passed with zero warnings/errors;
server DLLs differ and SDK DLLs are byte-identical. The before/after server SHA-256 values are
`f59c15eecceed578fa28441341755890dd45b7b88411a7ffbe27b83298a10ee7` and
`1eee6aff873fc61cf81b435615f12c4ce5a290bce32eb2d05bd6b654a131b105`.

Three fresh-process pairs per regime: before/after, after/before, before/after. Keyed-auth test issuer; FULL commits,
admission, metering and in-flight revocation watch remain active. Read workload, 10,000 keys/size, 1 s warmup, 5 s cells.
The unrestricted run used all 16 logical CPUs, including efficiency cores. The second run reused those exact artifacts
with taskset 0-11 (six performance cores with SMT); the benchmark reports 12 CPUs. Both ran through ts-heavy-any nice -n 19.
96 cells total, zero operation errors. No repeat was discarded. Instrumentation and wall-time limits are described in ATTRIBUTION.md.
Pinned run 00:13:09–00:19:19 UTC, load1 1.29–4.06/16 host CPUs, CPU pressure avg10 0.98–4.10% at arm boundaries.
One-second vmstat over that window: idle median 86% (range 48–96%), I/O wait max 4%.
Host was selected at low load; the pressure includes the benchmark's own work. This is not a reserved-host certification.

Rebuild/re-run the pinned method with `TS_HEAVY_HOST=delta ts-heavy-any nice -n 19 taskset -c 0-11 bash
benchmark/dotnet/VsTurso/run-etag.sh <same-sdk-checkout> 0f56119a3 benchmark/bench/etag-pairs-repro
--repo terrascale --branch fix/api-etag-reuse`. Raw .md/.log/.host files, hashes, topology and affinity remain in the
managed delta checkout under benchmark/bench/etag-pairs-delta and etag-pairs-delta-pcores.

Earlier mariogk-dev attempts are not comparison evidence: the first copied a stale baseline after CA1068 (identical
server DLLs; labeled INVALID.txt); the corrected attempt encountered load1 67.90/46 and 46.36% CPU pressure, was stopped
and labeled CONTENDED.txt. Their partial files were retained. The runner now explicitly propagates failed builds and checks artifacts.

## Fixed-core API medians (min–max of three repeats)

| Size | c | Before p50 µs | After p50 µs | Before p99 µs | After p99 µs | Before/after CPU µs/op | Before/after alloc B/op |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| small | 1 | 280.8 (256.8–316.8) | 259.0 (228.0–261.6) | 901.2 (784.4–909.2) | 882.6 (857.5–890.8) | 607.3/589.4 | 31679/31481 |
| small | 16 | 218.3 (196.2–220.0) | 198.6 (197.7–217.4) | 2244.3 (2084.0–2250.5) | 2190.2 (2188.7–2294.0) | 143.2/143.1 | 30408/30304 |
| medium | 1 | 123.8 (118.7–124.0) | 119.6 (118.5–122.6) | 367.9 (345.9–399.2) | 357.1 (341.6–371.9) | 231.8/221.3 | 45878/45737 |
| medium | 16 | 266.5 (260.3–280.9) | 251.4 (239.4–259.8) | 2625.2 (2623.3–2632.6) | 2796.2 (2583.2–2910.9) | 174.1/176.7 | 45854/45759 |

## All unrestricted cells

| Arm | Pair | Size | c | Lane | ops/s | Client p50/p99 µs | Requests (sampled) | Server p50/p99 µs | Engine p50/p99 µs | Overhead p50/p99 µs | CPU µs/op | Alloc B/op | Errors |
| --- | ---: | --- | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| before | 1 | small | 1 | engine | 1009482 | 0.8/1.8 | — | — | — | — | 1.0 | 970 | 0 |
| before | 1 | small | 1 | api | 2880 | 318.1/1056.7 | 14399 (14399) | 203.2/699.1 | 4.4/202.6 | 198.0/642.2 | 629.5 | 31502 | 0 |
| before | 1 | small | 16 | engine | 802541 | 4.1/25.8 | — | — | — | — | 5.9 | 960 | 0 |
| before | 1 | small | 16 | api | 37039 | 282.0/3026.7 | 185202 (16384) | 115.9/2550.6 | 5.1/410.6 | 109.9/2426.0 | 241.1 | 30867 | 0 |
| before | 1 | medium | 1 | engine | 394861 | 1.7/5.6 | — | — | — | — | 3.0 | 5018 | 0 |
| before | 1 | medium | 1 | api | 4744 | 165.6/565.5 | 23718 (16384) | 96.5/388.5 | 4.5/16.4 | 92.0/372.5 | 310.1 | 45706 | 0 |
| before | 1 | medium | 16 | engine | 317723 | 7.1/1583.7 | — | — | — | — | 11.0 | 4835 | 0 |
| before | 1 | medium | 16 | api | 26034 | 381.7/3737.4 | 130188 (16384) | 161.1/3275.1 | 7.1/27.6 | 153.5/3242.3 | 319.5 | 45909 | 0 |
| after | 1 | small | 1 | engine | 1063510 | 0.7/1.6 | — | — | — | — | 1.0 | 969 | 0 |
| after | 1 | small | 1 | api | 2390 | 331.2/1176.9 | 11948 (11948) | 213.0/765.0 | 4.7/192.3 | 206.5/729.0 | 744.6 | 31549 | 0 |
| after | 1 | small | 16 | engine | 750869 | 4.2/26.5 | — | — | — | — | 6.5 | 962 | 0 |
| after | 1 | small | 16 | api | 37944 | 282.4/2843.7 | 189734 (16384) | 116.1/2425.2 | 5.5/18.4 | 110.4/2359.2 | 236.7 | 30345 | 0 |
| after | 1 | medium | 1 | engine | 478997 | 1.6/3.5 | — | — | — | — | 2.2 | 4849 | 0 |
| after | 1 | medium | 1 | api | 3480 | 261.5/594.8 | 17401 (16384) | 158.6/368.3 | 7.1/17.2 | 151.5/352.1 | 415.6 | 45651 | 0 |
| after | 1 | medium | 16 | engine | 289445 | 7.9/1798.3 | — | — | — | — | 12.0 | 4835 | 0 |
| after | 1 | medium | 16 | api | 25976 | 383.1/3603.5 | 129890 (16384) | 149.6/3099.9 | 7.2/28.9 | 142.2/3060.4 | 309.1 | 45802 | 0 |
| after | 2 | small | 1 | engine | 1065455 | 0.7/1.6 | — | — | — | — | 1.0 | 969 | 0 |
| after | 2 | small | 1 | api | 2588 | 340.7/1059.5 | 12938 (12938) | 221.7/755.8 | 5.0/254.5 | 215.4/704.5 | 710.4 | 31579 | 0 |
| after | 2 | small | 16 | engine | 698749 | 4.5/29.2 | — | — | — | — | 6.9 | 963 | 0 |
| after | 2 | small | 16 | api | 35897 | 297.0/2983.8 | 179497 (16384) | 122.5/2526.5 | 5.6/18.4 | 116.7/2499.2 | 244.2 | 30401 | 0 |
| after | 2 | medium | 1 | engine | 495878 | 1.6/3.3 | — | — | — | — | 2.2 | 4855 | 0 |
| after | 2 | medium | 1 | api | 4194 | 229.6/546.0 | 20972 (16384) | 136.5/360.4 | 6.3/16.7 | 130.1/344.1 | 354.2 | 45705 | 0 |
| after | 2 | medium | 16 | engine | 293107 | 7.8/1726.8 | — | — | — | — | 11.9 | 4836 | 0 |
| after | 2 | medium | 16 | api | 24686 | 398.0/3760.4 | 123440 (16384) | 160.3/3222.9 | 8.0/30.4 | 151.3/3183.5 | 315.2 | 45826 | 0 |
| before | 2 | small | 1 | engine | 1028083 | 0.8/1.7 | — | — | — | — | 1.0 | 970 | 0 |
| before | 2 | small | 1 | api | 2541 | 324.7/1099.9 | 12704 (12704) | 213.0/741.8 | 4.7/173.2 | 206.6/711.0 | 715.1 | 31576 | 0 |
| before | 2 | small | 16 | engine | 765578 | 4.5/27.3 | — | — | — | — | 6.4 | 961 | 0 |
| before | 2 | small | 16 | api | 35090 | 298.6/3030.2 | 175466 (16384) | 95.6/2301.3 | 4.7/14.6 | 90.7/2247.8 | 189.9 | 30308 | 0 |
| before | 2 | medium | 1 | engine | 356876 | 2.0/7.5 | — | — | — | — | 3.0 | 4825 | 0 |
| before | 2 | medium | 1 | api | 5661 | 142.1/470.8 | 28306 (16384) | 79.1/313.7 | 4.2/14.7 | 75.0/298.8 | 255.7 | 45680 | 0 |
| before | 2 | medium | 16 | engine | 344601 | 6.7/1190.1 | — | — | — | — | 10.6 | 5051 | 0 |
| before | 2 | medium | 16 | api | 26184 | 378.9/3666.3 | 130931 (16384) | 159.0/3154.4 | 7.5/27.8 | 151.1/3098.8 | 312.3 | 45889 | 0 |
| before | 3 | small | 1 | engine | 1073284 | 0.7/1.6 | — | — | — | — | 0.9 | 969 | 0 |
| before | 3 | small | 1 | api | 2439 | 327.4/1122.2 | 12194 (12194) | 218.3/761.2 | 4.5/166.9 | 211.5/733.8 | 741.3 | 31631 | 0 |
| before | 3 | small | 16 | engine | 796509 | 4.0/24.9 | — | — | — | — | 6.1 | 962 | 0 |
| before | 3 | small | 16 | api | 37431 | 287.1/2949.9 | 187162 (16384) | 120.6/2468.9 | 5.3/17.4 | 115.1/2443.7 | 245.3 | 30480 | 0 |
| before | 3 | medium | 1 | engine | 463294 | 1.6/3.7 | — | — | — | — | 2.4 | 4892 | 0 |
| before | 3 | medium | 1 | api | 3672 | 253.3/609.8 | 18358 (16384) | 154.3/379.4 | 6.5/16.7 | 147.7/363.0 | 399.6 | 45744 | 0 |
| before | 3 | medium | 16 | engine | 328672 | 7.3/1339.6 | — | — | — | — | 10.9 | 4833 | 0 |
| before | 3 | medium | 16 | api | 26952 | 370.8/3460.5 | 134763 (16384) | 155.1/3020.7 | 6.8/26.2 | 147.9/2984.1 | 307.0 | 45885 | 0 |
| after | 3 | small | 1 | engine | 1025621 | 0.7/1.8 | — | — | — | — | 1.0 | 971 | 0 |
| after | 3 | small | 1 | api | 2538 | 332.3/1118.7 | 12690 (12690) | 217.9/768.7 | 4.6/175.6 | 211.5/727.7 | 714.2 | 31511 | 0 |
| after | 3 | small | 16 | engine | 805037 | 3.9/25.9 | — | — | — | — | 6.0 | 960 | 0 |
| after | 3 | small | 16 | api | 35207 | 308.4/3050.6 | 176044 (16384) | 132.0/2666.5 | 5.8/18.9 | 126.0/2641.8 | 258.9 | 30372 | 0 |
| after | 3 | medium | 1 | engine | 462313 | 1.6/3.8 | — | — | — | — | 2.4 | 4876 | 0 |
| after | 3 | medium | 1 | api | 4016 | 235.8/582.9 | 20080 (16384) | 141.8/373.5 | 6.3/17.3 | 135.3/357.0 | 366.2 | 45646 | 0 |
| after | 3 | medium | 16 | engine | 302651 | 7.7/1639.9 | — | — | — | — | 11.6 | 4836 | 0 |
| after | 3 | medium | 16 | api | 26171 | 371.9/3583.1 | 130859 (16384) | 152.5/3089.0 | 7.1/28.0 | 144.5/3055.7 | 308.8 | 45801 | 0 |

## All p-cores cells

| Arm | Pair | Size | c | Lane | ops/s | Client p50/p99 µs | Requests (sampled) | Server p50/p99 µs | Engine p50/p99 µs | Overhead p50/p99 µs | CPU µs/op | Alloc B/op | Errors |
| --- | ---: | --- | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| before | 1 | small | 1 | engine | 1078312 | 0.7/1.5 | — | — | — | — | 0.9 | 968 | 0 |
| before | 1 | small | 1 | api | 3375 | 280.8/784.4 | 16877 (16384) | 196.4/562.1 | 4.4/174.6 | 190.1/518.9 | 553.0 | 31679 | 0 |
| before | 1 | small | 16 | engine | 1901663 | 1.5/8.1 | — | — | — | — | 2.4 | 972 | 0 |
| before | 1 | small | 16 | api | 52359 | 196.2/2244.3 | 261808 (16384) | 81.8/1915.7 | 3.8/8.9 | 77.8/1873.2 | 143.4 | 30452 | 0 |
| before | 1 | medium | 1 | engine | 498622 | 1.6/3.2 | — | — | — | — | 2.1 | 4835 | 0 |
| before | 1 | medium | 1 | api | 7044 | 118.7/345.9 | 35221 (16384) | 79.9/241.1 | 3.7/9.6 | 76.2/232.3 | 215.0 | 45878 | 0 |
| before | 1 | medium | 16 | engine | 459006 | 4.8/764.0 | — | — | — | — | 7.6 | 4829 | 0 |
| before | 1 | medium | 16 | api | 37215 | 266.5/2623.3 | 186087 (16384) | 93.8/2146.7 | 4.7/11.3 | 88.7/2114.3 | 170.7 | 45854 | 0 |
| after | 1 | small | 1 | engine | 1076426 | 0.7/1.6 | — | — | — | — | 0.9 | 969 | 0 |
| after | 1 | small | 1 | api | 3354 | 228.0/882.6 | 16771 (16384) | 163.7/633.1 | 4.4/136.8 | 158.0/617.1 | 560.0 | 31481 | 0 |
| after | 1 | small | 16 | engine | 1882284 | 1.6/9.0 | — | — | — | — | 2.5 | 970 | 0 |
| after | 1 | small | 16 | api | 50073 | 217.4/2188.7 | 250370 (16384) | 80.3/1741.8 | 3.7/8.9 | 76.4/1703.8 | 141.7 | 30301 | 0 |
| after | 1 | medium | 1 | engine | 521317 | 1.6/2.7 | — | — | — | — | 2.0 | 4822 | 0 |
| after | 1 | medium | 1 | api | 7047 | 118.5/371.9 | 35237 (16384) | 79.5/250.6 | 3.7/10.6 | 75.8/238.3 | 219.5 | 45757 | 0 |
| after | 1 | medium | 16 | engine | 469649 | 4.4/990.2 | — | — | — | — | 7.2 | 4831 | 0 |
| after | 1 | medium | 16 | api | 36996 | 251.4/2910.9 | 184995 (16384) | 94.7/2475.1 | 4.7/12.7 | 89.6/2446.0 | 182.0 | 45764 | 0 |
| after | 2 | small | 1 | engine | 1078989 | 0.7/1.6 | — | — | — | — | 0.9 | 968 | 0 |
| after | 2 | small | 1 | api | 3161 | 259.0/857.5 | 15804 (15804) | 185.1/625.2 | 4.4/151.9 | 176.5/601.5 | 589.4 | 31566 | 0 |
| after | 2 | small | 16 | engine | 1854034 | 1.5/9.8 | — | — | — | — | 2.4 | 973 | 0 |
| after | 2 | small | 16 | api | 51636 | 197.7/2294.0 | 258189 (16384) | 82.3/1938.5 | 4.0/9.3 | 78.1/1875.3 | 145.1 | 30304 | 0 |
| after | 2 | medium | 1 | engine | 509592 | 1.6/3.1 | — | — | — | — | 2.1 | 4825 | 0 |
| after | 2 | medium | 1 | api | 6653 | 122.6/357.1 | 33264 (16384) | 82.4/248.9 | 4.0/10.9 | 78.4/238.5 | 230.3 | 45737 | 0 |
| after | 2 | medium | 16 | engine | 436652 | 4.7/1085.3 | — | — | — | — | 7.7 | 4834 | 0 |
| after | 2 | medium | 16 | api | 38078 | 259.8/2583.2 | 190397 (16384) | 90.5/2154.5 | 4.5/12.3 | 85.7/2101.4 | 169.4 | 45751 | 0 |
| before | 2 | small | 1 | engine | 1059390 | 0.7/1.7 | — | — | — | — | 1.0 | 969 | 0 |
| before | 2 | small | 1 | api | 2728 | 316.8/901.2 | 13641 (13641) | 221.4/655.0 | 4.6/136.7 | 215.0/633.3 | 660.7 | 31770 | 0 |
| before | 2 | small | 16 | engine | 1897133 | 1.5/8.7 | — | — | — | — | 2.4 | 972 | 0 |
| before | 2 | small | 16 | api | 49210 | 218.3/2250.5 | 246056 (16384) | 81.3/1852.0 | 3.6/8.2 | 77.5/1826.6 | 143.2 | 30390 | 0 |
| before | 2 | medium | 1 | engine | 505944 | 1.6/3.2 | — | — | — | — | 2.1 | 4826 | 0 |
| before | 2 | medium | 1 | api | 6533 | 124.0/399.2 | 32666 (16384) | 83.8/267.3 | 3.9/10.6 | 80.0/256.5 | 231.8 | 45795 | 0 |
| before | 2 | medium | 16 | engine | 440937 | 4.8/1107.7 | — | — | — | — | 7.8 | 4834 | 0 |
| before | 2 | medium | 16 | api | 37807 | 260.3/2632.6 | 189052 (16384) | 94.9/2199.2 | 4.4/11.5 | 90.1/2178.7 | 174.1 | 45829 | 0 |
| before | 3 | small | 1 | engine | 1037690 | 0.7/1.7 | — | — | — | — | 1.0 | 969 | 0 |
| before | 3 | small | 1 | api | 3121 | 256.8/909.2 | 15605 (15605) | 182.1/646.4 | 4.4/156.5 | 176.5/625.2 | 607.3 | 31359 | 0 |
| before | 3 | small | 16 | engine | 2006551 | 1.5/8.6 | — | — | — | — | 2.4 | 970 | 0 |
| before | 3 | small | 16 | api | 50238 | 220.0/2084.0 | 251202 (16384) | 80.7/1666.0 | 3.8/8.8 | 76.6/1631.4 | 140.1 | 30408 | 0 |
| before | 3 | medium | 1 | engine | 518110 | 1.6/3.0 | — | — | — | — | 2.0 | 4825 | 0 |
| before | 3 | medium | 1 | api | 6628 | 123.8/367.9 | 33141 (16384) | 84.8/256.8 | 4.1/11.0 | 80.7/244.7 | 233.4 | 46185 | 0 |
| before | 3 | medium | 16 | engine | 413284 | 5.2/1171.7 | — | — | — | — | 8.0 | 4807 | 0 |
| before | 3 | medium | 16 | api | 35820 | 280.9/2625.2 | 179106 (16384) | 95.4/2119.3 | 5.0/14.9 | 90.1/2065.7 | 178.6 | 46030 | 0 |
| after | 3 | small | 1 | engine | 1011203 | 0.8/1.7 | — | — | — | — | 1.0 | 970 | 0 |
| after | 3 | small | 1 | api | 3045 | 261.6/890.8 | 15227 (15227) | 187.4/647.7 | 4.6/119.5 | 180.6/627.6 | 615.7 | 31481 | 0 |
| after | 3 | small | 16 | engine | 2077396 | 1.4/8.1 | — | — | — | — | 2.3 | 969 | 0 |
| after | 3 | small | 16 | api | 52534 | 198.6/2190.2 | 262682 (16384) | 81.2/1787.2 | 4.0/9.1 | 77.0/1710.7 | 143.1 | 30325 | 0 |
| after | 3 | medium | 1 | engine | 492464 | 1.6/3.3 | — | — | — | — | 2.1 | 4833 | 0 |
| after | 3 | medium | 1 | api | 6927 | 119.6/341.6 | 34637 (16384) | 80.5/240.9 | 4.1/10.8 | 76.3/230.5 | 221.3 | 45711 | 0 |
| after | 3 | medium | 16 | engine | 422858 | 5.0/1087.9 | — | — | — | — | 8.1 | 4833 | 0 |
| after | 3 | medium | 16 | api | 38473 | 239.4/2796.2 | 192373 (16384) | 93.7/2424.3 | 4.8/12.3 | 88.5/2351.0 | 176.7 | 45759 | 0 |
