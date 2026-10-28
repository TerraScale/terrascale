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

## Lower-load fixed-core verification, 2026-10-28 UTC

Three additional alternating pairs reused the exact verified before/after server and identical SDK artifacts above,
through `TS_HEAVY_HOST=delta ts-heavy-any nice -n 19 bash -c ... --repo terrascale --branch fix/api-etag-reuse`.
Each benchmark process used taskset 0-11, the same keyed auth, 1 s warmup and 5 s cells. All six arms passed;
48 cells, zero operation errors. SHA-256 checks passed before execution. No arm or cell was discarded.
Raw cells, logs, artifact hashes, per-arm process CPU snapshots, topology and one-second vmstat are retained in the
managed delta checkout at `benchmark/bench/etag-pairs-delta-quiet/`. These are another controlled artifact comparison,
not measurements of a deployed server. The branch at launch was `2d81a3a65`; source/artifact identities remain as above.

Window 00:30:39–00:36:50 UTC; initial load1 1.03/16 CPUs and CPU pressure avg10 0.07%.
Arm-boundary load1 reached 5.20 and CPU pressure avg10 3.99%, including the benchmark itself.
vmstat idle median 85% (range 47–93%), I/O wait maximum 9%. The existing compiler accumulated about one CPU second
in the first arm. This was a lightly loaded shared host, without exclusive-host isolation.
Latency ranges still overlap. Small c=1 median p50 increased, while median allocation fell in all four configurations;
there is no uniform latency speedup. The first API size still runs slower than the later medium size at c=1.
The 1 s warmup and fixed size order do not rule out startup/tiering effects; no attribution of that difference is claimed.

| Size | c | Before p50 µs | After p50 µs | Before p99 µs | After p99 µs | Before/after CPU µs/op | Before/after alloc B/op |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| small | 1 | 268.9 (247.1–310.1) | 276.0 (274.3–277.5) | 905.0 (874.3–938.3) | 868.9 (503.8–878.7) | 631.3/605.7 | 31659/31567 |
| small | 16 | 217.9 (193.2–218.6) | 217.0 (193.6–222.9) | 2255.0 (2178.6–2267.0) | 2198.6 (2149.4–2256.3) | 142.6/143.2 | 30381/30295 |
| medium | 1 | 127.5 (120.3–164.9) | 121.0 (104.4–121.0) | 390.8 (347.6–393.4) | 334.9 (248.2–360.6) | 239.5/219.6 | 45809/45718 |
| medium | 16 | 260.9 (239.1–265.5) | 254.8 (252.1–265.9) | 2621.4 (2294.0–2665.3) | 2603.1 (2550.2–2754.2) | 171.5/166.3 | 45867/45749 |

| Arm | Pair | Size | c | Lane | ops/s | Client p50/p99 µs | Requests (sampled) | Server p50/p99 µs | Engine p50/p99 µs | Overhead p50/p99 µs | CPU µs/op | Alloc B/op | Errors |
| --- | ---: | --- | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| before | 1 | small | 1 | engine | 1045245 | 0.7/1.7 | — | — | — | — | 1.0 | 969 | 0 |
| before | 1 | small | 1 | api | 2784 | 310.1/905.0 | 13921 (13921) | 216.9/638.7 | 4.4/152.6 | 210.8/619.8 | 655.8 | 31778 | 0 |
| before | 1 | small | 16 | engine | 2000416 | 1.5/7.8 | — | — | — | — | 2.4 | 970 | 0 |
| before | 1 | small | 16 | api | 52847 | 193.2/2255.0 | 264242 (16384) | 81.7/1892.9 | 3.6/8.2 | 77.8/1851.3 | 143.6 | 30401 | 0 |
| before | 1 | medium | 1 | engine | 483308 | 1.7/3.4 | — | — | — | — | 2.2 | 4840 | 0 |
| before | 1 | medium | 1 | api | 6229 | 127.5/390.8 | 31147 (16384) | 86.4/273.3 | 4.0/11.0 | 82.4/262.3 | 239.5 | 45785 | 0 |
| before | 1 | medium | 16 | engine | 447686 | 4.8/888.0 | — | — | — | — | 7.7 | 4829 | 0 |
| before | 1 | medium | 16 | api | 37296 | 260.9/2665.3 | 186490 (16384) | 93.4/2206.0 | 4.3/10.8 | 88.8/2164.5 | 171.5 | 45799 | 0 |
| after | 1 | small | 1 | engine | 1056415 | 0.7/1.7 | — | — | — | — | 1.0 | 969 | 0 |
| after | 1 | small | 1 | api | 3073 | 277.5/868.9 | 15363 (15363) | 196.2/622.4 | 4.5/159.9 | 190.4/595.8 | 605.7 | 31567 | 0 |
| after | 1 | small | 16 | engine | 2022261 | 1.4/8.4 | — | — | — | — | 2.3 | 969 | 0 |
| after | 1 | small | 16 | api | 49527 | 217.0/2149.4 | 247640 (16384) | 78.9/1705.2 | 3.9/9.4 | 74.8/1653.2 | 139.5 | 30241 | 0 |
| after | 1 | medium | 1 | engine | 513132 | 1.6/3.2 | — | — | — | — | 2.0 | 4824 | 0 |
| after | 1 | medium | 1 | api | 6995 | 121.0/334.9 | 34976 (16384) | 80.7/226.9 | 4.0/10.1 | 76.7/217.2 | 219.6 | 45718 | 0 |
| after | 1 | medium | 16 | engine | 413402 | 5.1/1269.2 | — | — | — | — | 8.0 | 4805 | 0 |
| after | 1 | medium | 16 | api | 36299 | 265.9/2754.2 | 181506 (16384) | 92.3/2240.7 | 4.8/15.1 | 87.1/2173.6 | 179.2 | 45885 | 0 |
| after | 2 | small | 1 | engine | 1090962 | 0.7/1.5 | — | — | — | — | 0.9 | 968 | 0 |
| after | 2 | small | 1 | api | 2970 | 274.3/878.7 | 14851 (14851) | 196.6/646.1 | 4.4/167.0 | 190.4/624.1 | 625.8 | 31572 | 0 |
| after | 2 | small | 16 | engine | 2079250 | 1.4/7.8 | — | — | — | — | 2.2 | 969 | 0 |
| after | 2 | small | 16 | api | 52859 | 193.6/2256.3 | 264297 (16384) | 81.0/1926.1 | 3.7/8.7 | 77.1/1905.6 | 143.2 | 30353 | 0 |
| after | 2 | medium | 1 | engine | 506367 | 1.6/3.1 | — | — | — | — | 2.1 | 4832 | 0 |
| after | 2 | medium | 1 | api | 6531 | 121.0/360.6 | 32653 (16384) | 82.1/251.6 | 3.8/10.1 | 78.4/241.5 | 235.2 | 45706 | 0 |
| after | 2 | medium | 16 | engine | 453826 | 4.8/786.9 | — | — | — | — | 7.6 | 4830 | 0 |
| after | 2 | medium | 16 | api | 39000 | 252.1/2550.2 | 195012 (16384) | 89.6/2143.9 | 4.3/10.9 | 85.0/2096.8 | 166.0 | 45747 | 0 |
| before | 2 | small | 1 | engine | 1076890 | 0.7/1.6 | — | — | — | — | 0.9 | 969 | 0 |
| before | 2 | small | 1 | api | 3195 | 247.1/874.3 | 15977 (15977) | 176.8/630.4 | 4.4/149.3 | 170.3/612.6 | 590.0 | 31588 | 0 |
| before | 2 | small | 16 | engine | 2101965 | 1.4/7.9 | — | — | — | — | 2.2 | 966 | 0 |
| before | 2 | small | 16 | api | 49891 | 217.9/2178.6 | 249467 (16384) | 81.2/1842.1 | 3.6/8.7 | 77.4/1799.6 | 141.7 | 30381 | 0 |
| before | 2 | medium | 1 | engine | 515069 | 1.6/2.6 | — | — | — | — | 2.1 | 4854 | 0 |
| before | 2 | medium | 1 | api | 5455 | 164.9/393.4 | 27273 (16384) | 109.8/274.3 | 4.8/11.5 | 105.0/262.9 | 282.7 | 45809 | 0 |
| before | 2 | medium | 16 | engine | 420800 | 4.9/1140.5 | — | — | — | — | 8.0 | 4828 | 0 |
| before | 2 | medium | 16 | api | 39256 | 265.5/2294.0 | 196293 (16384) | 89.7/1853.3 | 4.1/9.9 | 85.3/1802.6 | 157.3 | 45867 | 0 |
| before | 3 | small | 1 | engine | 1076595 | 0.7/1.6 | — | — | — | — | 0.9 | 969 | 0 |
| before | 3 | small | 1 | api | 2939 | 268.9/938.3 | 14696 (14696) | 192.5/668.6 | 4.4/150.8 | 185.7/643.3 | 631.3 | 31659 | 0 |
| before | 3 | small | 16 | engine | 2095447 | 1.4/8.1 | — | — | — | — | 2.3 | 968 | 0 |
| before | 3 | small | 16 | api | 49290 | 218.6/2267.0 | 246458 (16384) | 80.6/1881.3 | 3.6/8.6 | 76.7/1826.7 | 142.6 | 30357 | 0 |
| before | 3 | medium | 1 | engine | 503041 | 1.6/3.3 | — | — | — | — | 2.1 | 4835 | 0 |
| before | 3 | medium | 1 | api | 6906 | 120.3/347.6 | 34530 (16384) | 81.4/239.5 | 3.7/9.7 | 77.7/229.2 | 219.0 | 45815 | 0 |
| before | 3 | medium | 16 | engine | 451535 | 4.9/1149.3 | — | — | — | — | 7.8 | 4834 | 0 |
| before | 3 | medium | 16 | api | 39620 | 239.1/2621.4 | 198118 (16384) | 96.5/2170.2 | 4.6/12.2 | 91.5/2147.2 | 179.2 | 45894 | 0 |
| after | 3 | small | 1 | engine | 1060843 | 0.8/1.6 | — | — | — | — | 1.0 | 969 | 0 |
| after | 3 | small | 1 | api | 3650 | 276.0/503.8 | 18250 (16384) | 195.6/383.1 | 4.5/140.5 | 190.3/317.2 | 515.6 | 31554 | 0 |
| after | 3 | small | 16 | engine | 2028376 | 1.6/7.8 | — | — | — | — | 2.4 | 970 | 0 |
| after | 3 | small | 16 | api | 49068 | 222.9/2198.6 | 245351 (16384) | 81.2/1790.4 | 3.7/8.7 | 77.3/1755.3 | 143.8 | 30295 | 0 |
| after | 3 | medium | 1 | engine | 531084 | 1.6/2.6 | — | — | — | — | 2.0 | 4821 | 0 |
| after | 3 | medium | 1 | api | 8815 | 104.4/248.2 | 44077 (16384) | 69.4/165.2 | 3.4/7.3 | 65.9/158.0 | 173.7 | 45770 | 0 |
| after | 3 | medium | 16 | engine | 416683 | 4.9/1210.0 | — | — | — | — | 7.9 | 4826 | 0 |
| after | 3 | medium | 16 | api | 38129 | 254.8/2603.1 | 190646 (16384) | 91.0/2203.2 | 4.6/11.0 | 86.1/2167.4 | 166.3 | 45749 | 0 |

## Ten-second-warmup comparison, 2026-10-28 UTC

Three further alternating pairs used a fixed 10 s warmup and 5 s cells, after rebasing the common harness/server base.
Harness/after source `3a0ce924a`, before server `b42181227`, SDK `d622039` in both arms. The new source base and
warmup differ from earlier batches; comparisons between batches cannot be attributed to the ETag change.
Both Release builds passed with zero warnings/errors; server src differs only in GetDocumentEndpoint.cs and SDK
DLLs are byte-identical. Server SHA-256 before/after:
`a67e1cda1e00c2a250664cdcafcc49885fd7f26d3856e026653af1d8b66fb7c6` /
`a1cd62afa663010e4eb1e4b93dfab78174514f8b5f443916fbeecfc5d72e7ebd`.

All six arms passed: 48 cells, zero operation errors; every arm and cell is retained below.
Window 00:41:09–00:54:46 UTC, taskset 0-11 on delta, ts-heavy-any nice -n 19. Arm-boundary load1 3.87–7.92/16 CPUs,
CPU pressure avg10 4.68–13.49% (including build decay and benchmark work). Concurrent compiler activity was observed.
This is diagnostic evidence, not quiet-host qualification. Raw logs/cells/hashes and timestamped vmstat remain under
`benchmark/bench/etag-pairs-delta-warm10/` in the managed delta checkout.

The first small c=1 path no longer has the earlier large inversion relative to medium, but no isolated cause is proven.
Within these matched before/after arms, latency ranges overlap and small c=1 median p50 increased. Allocation medians
fell in all four API configurations. No uniform latency speedup or <10% server-over-engine result is established.
Reproduce with the existing run-etag.sh command above, using before ref b42181227 and this ten-second-warmup source.

| Size | c | Before p50 µs | After p50 µs | Before p99 µs | After p99 µs | Before/after CPU µs/op | Before/after alloc B/op |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| small | 1 | 107.8 (107.3–120.5) | 110.9 (103.7–111.5) | 326.5 (324.7–326.9) | 314.8 (275.3–321.0) | 199.5/211.1 | 30744/30637 |
| small | 16 | 223.6 (214.8–234.3) | 221.3 (218.9–224.5) | 2284.3 (2052.9–2471.3) | 2296.2 (2253.5–2304.8) | 143.4/142.3 | 30463/30343 |
| medium | 1 | 133.4 (127.9–142.1) | 128.9 (117.8–164.4) | 399.2 (393.0–405.5) | 391.2 (353.4–628.3) | 250.3/246.6 | 46527/46450 |
| medium | 16 | 262.9 (236.5–268.9) | 262.4 (248.4–289.1) | 2583.4 (2517.8–2619.2) | 2484.5 (2448.7–2600.7) | 175.1/169.4 | 46008/45884 |

| Arm | Pair | Size | c | Lane | ops/s | Client p50/p99 µs | Requests (sampled) | Server p50/p99 µs | Engine p50/p99 µs | Overhead p50/p99 µs | CPU µs/op | Alloc B/op | Errors |
| --- | ---: | --- | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| before | 1 | small | 1 | engine | 1059500 | 0.7/1.6 | — | — | — | — | 1.0 | 969 | 0 |
| before | 1 | small | 1 | api | 7834 | 107.3/326.9 | 39168 (16384) | 69.7/234.5 | 3.2/13.2 | 66.5/211.5 | 199.5 | 30783 | 0 |
| before | 1 | small | 16 | engine | 1795875 | 1.6/11.5 | — | — | — | — | 2.6 | 973 | 0 |
| before | 1 | small | 16 | api | 43965 | 234.3/2471.3 | 219834 (16384) | 82.4/1882.9 | 3.9/598.8 | 77.9/1742.6 | 154.1 | 31425 | 0 |
| before | 1 | medium | 1 | engine | 524790 | 1.6/2.5 | — | — | — | — | 1.9 | 4809 | 0 |
| before | 1 | medium | 1 | api | 6321 | 127.9/405.5 | 31604 (16384) | 87.2/295.8 | 4.1/125.5 | 82.9/253.7 | 237.0 | 46660 | 0 |
| before | 1 | medium | 16 | engine | 377052 | 5.4/1378.4 | — | — | — | — | 8.6 | 4802 | 0 |
| before | 1 | medium | 16 | api | 37120 | 262.9/2619.2 | 185611 (16384) | 95.4/2163.8 | 5.0/12.7 | 90.1/2114.6 | 175.4 | 46049 | 0 |
| after | 1 | small | 1 | engine | 1065260 | 0.7/1.6 | — | — | — | — | 1.0 | 969 | 0 |
| after | 1 | small | 1 | api | 7538 | 111.5/314.8 | 37691 (16384) | 73.1/228.6 | 3.5/12.5 | 69.5/202.1 | 211.1 | 30640 | 0 |
| after | 1 | small | 16 | engine | 1975461 | 1.5/8.0 | — | — | — | — | 2.3 | 971 | 0 |
| after | 1 | small | 16 | api | 49157 | 221.3/2253.5 | 245795 (16384) | 80.9/1742.3 | 4.1/10.1 | 76.6/1699.9 | 142.3 | 30343 | 0 |
| after | 1 | medium | 1 | engine | 537960 | 1.6/2.6 | — | — | — | — | 1.9 | 4809 | 0 |
| after | 1 | medium | 1 | api | 7016 | 117.8/353.4 | 35079 (16384) | 78.8/253.8 | 4.0/82.6 | 74.6/229.4 | 218.3 | 46450 | 0 |
| after | 1 | medium | 16 | engine | 425656 | 4.8/1270.5 | — | — | — | — | 7.6 | 4811 | 0 |
| after | 1 | medium | 16 | api | 39163 | 248.4/2600.7 | 195822 (16384) | 93.5/2218.2 | 4.9/13.6 | 88.3/2163.0 | 175.5 | 45909 | 0 |
| after | 2 | small | 1 | engine | 1078076 | 0.7/1.5 | — | — | — | — | 0.9 | 968 | 0 |
| after | 2 | small | 1 | api | 7415 | 110.9/321.0 | 37075 (16384) | 73.2/229.4 | 3.1/11.3 | 70.0/209.9 | 215.5 | 30637 | 0 |
| after | 2 | small | 16 | engine | 1946269 | 1.5/8.5 | — | — | — | — | 2.3 | 971 | 0 |
| after | 2 | small | 16 | api | 49224 | 218.9/2304.8 | 246134 (16384) | 80.4/1780.9 | 3.6/8.5 | 76.6/1769.1 | 141.5 | 30347 | 0 |
| after | 2 | medium | 1 | engine | 530328 | 1.6/2.7 | — | — | — | — | 1.9 | 4809 | 0 |
| after | 2 | medium | 1 | api | 4996 | 164.4/628.3 | 24982 (16384) | 110.6/501.1 | 5.0/253.9 | 93.8/271.8 | 313.4 | 54466 | 0 |
| after | 2 | medium | 16 | engine | 380365 | 5.2/1316.2 | — | — | — | — | 8.3 | 4801 | 0 |
| after | 2 | medium | 16 | api | 38475 | 262.4/2484.5 | 192385 (16384) | 91.1/1992.7 | 4.4/11.5 | 86.3/1938.3 | 169.4 | 45884 | 0 |
| before | 2 | small | 1 | engine | 1089416 | 0.7/1.5 | — | — | — | — | 0.9 | 968 | 0 |
| before | 2 | small | 1 | api | 6742 | 120.5/324.7 | 33710 (16384) | 80.3/231.0 | 3.4/11.6 | 76.7/217.1 | 235.1 | 30744 | 0 |
| before | 2 | small | 16 | engine | 1719034 | 1.6/10.6 | — | — | — | — | 2.6 | 972 | 0 |
| before | 2 | small | 16 | api | 48809 | 223.6/2284.3 | 244054 (16384) | 81.4/1757.7 | 3.7/9.0 | 77.5/1710.2 | 143.4 | 30463 | 0 |
| before | 2 | medium | 1 | engine | 532223 | 1.6/2.7 | — | — | — | — | 1.9 | 4809 | 0 |
| before | 2 | medium | 1 | api | 6049 | 133.4/393.0 | 30245 (16384) | 91.5/280.0 | 4.2/80.5 | 87.1/248.4 | 250.3 | 46527 | 0 |
| before | 2 | medium | 16 | engine | 413106 | 4.9/1223.6 | — | — | — | — | 7.7 | 4804 | 0 |
| before | 2 | medium | 16 | api | 37259 | 268.9/2583.4 | 186301 (16384) | 94.8/2064.3 | 4.5/12.0 | 90.0/2021.2 | 175.1 | 46008 | 0 |
| before | 3 | small | 1 | engine | 928871 | 0.8/2.0 | — | — | — | — | 1.1 | 972 | 0 |
| before | 3 | small | 1 | api | 7710 | 107.8/326.5 | 38553 (16384) | 70.6/224.3 | 3.0/8.9 | 67.6/213.6 | 198.3 | 30370 | 0 |
| before | 3 | small | 16 | engine | 1902943 | 1.6/8.6 | — | — | — | — | 2.5 | 972 | 0 |
| before | 3 | small | 16 | api | 51202 | 214.8/2052.9 | 256022 (16384) | 80.1/1678.8 | 3.6/8.4 | 76.2/1646.5 | 137.8 | 30353 | 0 |
| before | 3 | medium | 1 | engine | 540449 | 1.6/2.6 | — | — | — | — | 1.9 | 4808 | 0 |
| before | 3 | medium | 1 | api | 5634 | 142.1/399.2 | 28169 (16384) | 97.1/279.7 | 4.4/11.7 | 92.7/262.4 | 270.5 | 45859 | 0 |
| before | 3 | medium | 16 | engine | 413637 | 4.8/1250.0 | — | — | — | — | 7.7 | 4803 | 0 |
| before | 3 | medium | 16 | api | 40395 | 236.5/2517.8 | 201985 (16384) | 96.3/2113.4 | 4.6/11.0 | 91.4/2076.6 | 175.1 | 45830 | 0 |
| after | 3 | small | 1 | engine | 1055518 | 0.7/1.6 | — | — | — | — | 1.0 | 969 | 0 |
| after | 3 | small | 1 | api | 8655 | 103.7/275.3 | 43277 (16384) | 67.1/207.4 | 2.8/9.0 | 64.2/174.4 | 183.3 | 30597 | 0 |
| after | 3 | small | 16 | engine | 1918752 | 1.5/9.3 | — | — | — | — | 2.4 | 970 | 0 |
| after | 3 | small | 16 | api | 48087 | 224.5/2296.2 | 240448 (16384) | 80.8/1850.1 | 3.7/9.0 | 76.9/1813.2 | 143.7 | 30334 | 0 |
| after | 3 | medium | 1 | engine | 532577 | 1.6/2.5 | — | — | — | — | 1.9 | 4809 | 0 |
| after | 3 | medium | 1 | api | 6175 | 128.9/391.2 | 30877 (16384) | 88.0/270.8 | 4.1/74.7 | 83.7/240.1 | 246.6 | 46439 | 0 |
| after | 3 | medium | 16 | engine | 418996 | 5.0/1101.3 | — | — | — | — | 7.7 | 4807 | 0 |
| after | 3 | medium | 16 | api | 36458 | 289.1/2448.7 | 182297 (16384) | 91.1/1929.8 | 4.6/13.8 | 86.1/1874.7 | 165.4 | 45848 | 0 |
