# API request overhead: keyed attribution baseline, 2026-10-27

Diagnostic only: no qualification or speedup. Original server-over-engine target <10% at c=1/16 remains unmet; HTTP cells are far outside it. The host became contended; every repeat is retained. This predates the duplicate-ETag candidate.
Source `a11f18152`, beta 12 vCPU, .NET 11.0.0-rc.1.26425.128, production workstation/concurrent GC and tiered PGO. Three repeats; 256 B/4 KiB JSON, 10,000 preloaded keys, 1 s warmup, 5 s cells; all 48 cells have zero operation errors.
Synthetic project token has key_id/key_gen and a 12 h benchmark lifetime against the test-only issuer. Auth authority/watch, admission and metering are included; production ts-auth round trips are not measured.
Hosting activity excludes pre-host Kestrel parsing/socket work. Engine is per-request GetAsync wall time; overhead is server minus engine, sorted independently, with at most 16,384 paired samples per cell. Instrumentation affects allocation/scheduling and is identical across HTTP lanes.
`api` consumes bytes, `api-json` parses response JSON, `sdk` uses the document SDK. In-process `engine` unwraps the decorator and has no server/engine/overhead attribution columns.
Host 23:26:45Z→23:32:06Z: load1 1.65→6.89, CPU pressure avg10 0.05%→7.53%. Full host evidence/raw JSON retained at `/tmp/perf-api-keyed-baseline.{md,host}` on delta; managed originals `benchmark/bench/2026-10-27-perf-api-keyed-baseline.*` in the attribution checkout.

| Size | c | Lane | Repeat | ops/s | Client p50/p99 µs | Server p50/p99 µs | Engine p50/p99 µs | Overhead p50/p99 µs | CPU µs/op | Alloc B/op | Errors |
| --- | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| small | 1 | engine | 1 | 871,844 | 0.9/1.9 | — | — | — | 1.2 | 975 | 0 |
| small | 1 | api | 1 | 2,516 | 371.2/1201.8 | 240.4/628.9 | 5.7/14.7 | 235.3/606.1 | 690.2 | 31,044 | 0 |
| small | 1 | sdk | 1 | 2,163 | 266.8/4005.2 | 124.6/1222.4 | 5.4/179.2 | 118.8/584.2 | 398.9 | 33,415 | 0 |
| small | 1 | api-json | 1 | 1,311 | 267.5/4684.6 | 126.2/1609.3 | 5.4/193.3 | 120.4/677.7 | 384.8 | 30,459 | 0 |
| small | 16 | engine | 1 | 1,472,098 | 2.1/19.5 | — | — | — | 3.1 | 962 | 0 |
| small | 16 | api | 1 | 14,619 | 528.1/8565.3 | 120.3/3220.7 | 5.4/208.3 | 114.5/3111.7 | 237.9 | 30,464 | 0 |
| small | 16 | sdk | 1 | 26,152 | 448.6/2812.7 | 117.8/2013.2 | 5.3/12.3 | 112.3/1982.8 | 231.2 | 32,917 | 0 |
| small | 16 | api-json | 1 | 26,140 | 420.1/3250.9 | 117.5/2387.3 | 5.3/1097.3 | 111.4/2034.2 | 237.6 | 31,982 | 0 |
| medium | 1 | engine | 1 | 436,135 | 1.8/3.8 | — | — | — | 2.4 | 4,856 | 0 |
| medium | 1 | api | 1 | 4,593 | 175.4/469.5 | 102.8/343.6 | 5.2/184.0 | 97.1/188.4 | 313.9 | 59,129 | 0 |
| medium | 1 | sdk | 1 | 5,143 | 177.9/416.9 | 99.1/283.8 | 4.8/145.6 | 94.3/187.3 | 268.3 | 56,944 | 0 |
| medium | 1 | api-json | 1 | 5,544 | 167.9/358.6 | 98.0/227.7 | 4.8/12.5 | 93.1/185.5 | 250.5 | 46,012 | 0 |
| medium | 16 | engine | 1 | 497,247 | 3.5/1411.0 | — | — | — | 5.7 | 4,829 | 0 |
| medium | 16 | api | 1 | 28,915 | 375.1/2368.7 | 121.2/1859.2 | 5.7/13.5 | 115.2/1841.9 | 233.6 | 45,702 | 0 |
| medium | 16 | sdk | 1 | 25,631 | 406.1/2508.5 | 121.6/1934.5 | 6.2/16.2 | 115.3/1880.1 | 251.7 | 55,763 | 0 |
| medium | 16 | api-json | 1 | 28,147 | 389.0/2480.1 | 123.4/1878.7 | 5.9/15.6 | 117.3/1849.3 | 240.3 | 46,001 | 0 |
| small | 1 | engine | 2 | 778,244 | 1.1/2.3 | — | — | — | 1.3 | 961 | 0 |
| small | 1 | api | 2 | 5,657 | 160.9/389.1 | 93.4/295.6 | 4.0/152.5 | 89.3/176.5 | 247.6 | 30,787 | 0 |
| small | 1 | sdk | 2 | 5,619 | 167.3/310.5 | 91.7/180.9 | 3.9/8.0 | 87.6/171.4 | 244.8 | 33,184 | 0 |
| small | 1 | api-json | 2 | 5,821 | 159.1/342.5 | 91.4/232.7 | 3.9/17.1 | 87.4/173.0 | 237.3 | 30,445 | 0 |
| small | 16 | engine | 2 | 1,438,137 | 2.0/16.8 | — | — | — | 3.0 | 965 | 0 |
| small | 16 | api | 2 | 19,157 | 610.4/3938.2 | 122.4/1888.8 | 5.5/13.8 | 116.6/1840.2 | 223.1 | 30,186 | 0 |
| small | 16 | sdk | 2 | 10,620 | 675.9/11489.8 | 122.1/5885.6 | 5.5/524.7 | 116.4/5517.2 | 254.3 | 33,218 | 0 |
| small | 16 | api-json | 2 | 6,944 | 1313.4/12990.5 | 127.8/7909.6 | 5.7/335.1 | 121.8/7458.6 | 258.7 | 30,587 | 0 |
| medium | 1 | engine | 2 | 265,530 | 2.7/6.0 | — | — | — | 3.5 | 4,809 | 0 |
| medium | 1 | api | 2 | 4,960 | 184.1/563.0 | 107.8/444.4 | 5.3/235.5 | 102.6/183.2 | 284.6 | 46,626 | 0 |
| medium | 1 | sdk | 2 | 4,962 | 185.9/395.0 | 102.3/208.8 | 5.1/12.0 | 97.4/189.8 | 276.7 | 55,851 | 0 |
| medium | 1 | api-json | 2 | 5,061 | 180.1/528.6 | 104.0/424.5 | 5.2/235.5 | 98.9/190.7 | 278.1 | 46,902 | 0 |
| medium | 16 | engine | 2 | 387,103 | 4.7/1533.5 | — | — | — | 7.4 | 4,825 | 0 |
| medium | 16 | api | 2 | 26,392 | 406.2/2723.6 | 122.7/2037.2 | 5.9/15.6 | 116.5/2001.5 | 231.8 | 45,691 | 0 |
| medium | 16 | sdk | 2 | 22,598 | 453.2/2983.2 | 123.7/2258.4 | 6.4/34.1 | 117.0/2205.7 | 257.4 | 56,033 | 0 |
| medium | 16 | api-json | 2 | 26,926 | 407.7/2567.1 | 123.4/1939.6 | 5.9/15.2 | 117.2/1906.4 | 233.8 | 45,759 | 0 |
| small | 1 | engine | 3 | 667,233 | 1.2/2.5 | — | — | — | 1.5 | 968 | 0 |
| small | 1 | api | 3 | 5,648 | 164.8/315.1 | 95.5/200.0 | 4.1/11.0 | 91.3/175.7 | 248.8 | 30,576 | 0 |
| small | 1 | sdk | 3 | 5,426 | 171.1/344.7 | 93.6/206.3 | 4.0/11.0 | 89.5/173.5 | 252.9 | 33,002 | 0 |
| small | 1 | api-json | 3 | 5,725 | 162.8/354.1 | 94.0/262.0 | 4.0/144.8 | 89.9/170.6 | 243.8 | 30,817 | 0 |
| small | 16 | engine | 3 | 1,497,405 | 2.1/19.4 | — | — | — | 3.0 | 961 | 0 |
| small | 16 | api | 3 | 31,861 | 372.3/2339.7 | 116.1/1805.0 | 5.2/14.1 | 110.5/1768.4 | 211.3 | 30,308 | 0 |
| small | 16 | sdk | 3 | 29,100 | 402.8/2545.9 | 116.7/1885.9 | 5.2/13.1 | 111.1/1832.5 | 228.3 | 33,062 | 0 |
| small | 16 | api-json | 3 | 31,722 | 369.3/2425.2 | 115.2/1876.6 | 5.2/10.9 | 109.8/1843.6 | 211.0 | 30,311 | 0 |
| medium | 1 | engine | 3 | 416,500 | 2.0/3.8 | — | — | — | 2.4 | 4,816 | 0 |
| medium | 1 | api | 3 | 5,536 | 170.1/302.9 | 101.2/192.9 | 5.0/10.2 | 96.3/184.6 | 252.2 | 45,628 | 0 |
| medium | 1 | sdk | 3 | 5,015 | 183.7/503.7 | 103.1/337.3 | 5.0/166.4 | 98.1/180.7 | 272.9 | 56,439 | 0 |
| medium | 1 | api-json | 3 | 5,413 | 172.4/347.6 | 101.7/248.5 | 5.0/12.4 | 96.6/189.9 | 257.4 | 46,331 | 0 |
| medium | 16 | engine | 3 | 494,881 | 3.7/1310.1 | — | — | — | 5.7 | 4,809 | 0 |
| medium | 16 | api | 3 | 26,681 | 416.2/2510.5 | 124.0/1859.9 | 5.8/18.3 | 117.8/1821.5 | 235.9 | 45,930 | 0 |
| medium | 16 | sdk | 3 | 23,253 | 458.4/2697.8 | 126.0/1979.5 | 6.4/15.4 | 119.3/1935.9 | 255.7 | 55,731 | 0 |
| medium | 16 | api-json | 3 | 26,642 | 416.5/2504.4 | 123.6/1875.6 | 5.8/14.5 | 117.5/1820.5 | 234.3 | 45,860 | 0 |
