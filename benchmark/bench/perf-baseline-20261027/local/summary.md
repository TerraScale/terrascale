# TerraScale performance baseline

Source: `58fae77420428e3c2b576ff9a62913d0b3a97abc`; host: `mariogk-dev`.
Main source: `9682f0ec3ffb934945dde49c277ccaef6ba08d0a`; CPUs: 46; starting load: [11.03662109375, 26.3447265625, 20.767578125].
Starting CPU pressure: `some avg10=0.01 avg60=0.70 avg300=7.51 total=1591426267`. Recorded 551 load/pressure observations, including build and preload.

Quietness is checked at admission; scheduler slots stay reserved. Recorded load/pressure and control repeat spreads must be inspected for unscheduled interference during measurement.
Three interleaved repeats; host reservation: True; status: complete, zero operation errors.

Q-PARITY read targets: c=1 each percentile ≤ 1.1 × (raw Turso + TCP echo); c=16 ops/s ≥ 0.9 × min(raw Turso, TCP echo). Applying the Wire budget to other endpoints is an aspirational comparison.

## Biggest gaps vs target

| Rank | Lane | Size | c | Metric | Observed | Target | Gap ratio |
| ---: | --- | --- | ---: | --- | ---: | ---: | ---: |
| 1 | sql | small | 16 | OpsPerSecond | 4.2 | 116605.6 | 27983.11× |
| 2 | hrana | small | 16 | OpsPerSecond | 4.2 | 116605.6 | 27763.24× |
| 3 | pg | small | 16 | OpsPerSecond | 4.5 | 116605.6 | 26103.79× |
| 4 | pg | medium | 16 | OpsPerSecond | 3.5 | 77359.9 | 22102.82× |
| 5 | sql | medium | 16 | OpsPerSecond | 4.1 | 77359.9 | 18868.26× |
| 6 | hrana | medium | 16 | OpsPerSecond | 4.4 | 77359.9 | 17714.65× |
| 7 | pg | small | 1 | P50Micros | 418549.6 | 163.0 | 2567.47× |
| 8 | sql | small | 1 | P50Micros | 410999.2 | 163.0 | 2521.16× |
| 9 | hrana | small | 1 | P50Micros | 409926.1 | 163.0 | 2514.58× |
| 10 | pg | medium | 1 | P50Micros | 413114.2 | 191.3 | 2159.62× |
| 11 | hrana | medium | 1 | P50Micros | 410768.6 | 191.3 | 2147.36× |
| 12 | sql | medium | 1 | P50Micros | 403928.1 | 191.3 | 2111.60× |
| 13 | pg | small | 1 | P99Micros | 660782.6 | 354.4 | 1864.41× |
| 14 | pg | medium | 1 | P99Micros | 706623.7 | 411.3 | 1718.07× |
| 15 | sql | small | 1 | P99Micros | 486938.8 | 354.4 | 1373.90× |
| 16 | hrana | medium | 1 | P99Micros | 545090.3 | 411.3 | 1325.32× |
| 17 | hrana | small | 1 | P99Micros | 459496.8 | 354.4 | 1296.48× |
| 18 | sql | medium | 1 | P99Micros | 487660.0 | 411.3 | 1185.68× |
| 19 | api-query | small | 1 | P99Micros | 3741.8 | 354.4 | 10.56× |
| 20 | api-query | medium | 1 | P99Micros | 3640.2 | 411.3 | 8.85× |
| 21 | api-query | small | 1 | P50Micros | 1365.8 | 163.0 | 8.38× |
| 22 | api-query | small | 16 | OpsPerSecond | 14068.8 | 116605.6 | 8.29× |
| 23 | api-query | medium | 1 | P50Micros | 1455.9 | 191.3 | 7.61× |
| 24 | api-query | medium | 16 | OpsPerSecond | 10491.6 | 77359.9 | 7.37× |
| 25 | api | small | 16 | OpsPerSecond | 21057.4 | 116605.6 | 5.54× |
| 26 | api | medium | 16 | OpsPerSecond | 15002.2 | 77359.9 | 5.16× |
| 27 | api | medium | 1 | P99Micros | 2062.0 | 411.3 | 5.01× |
| 28 | api | small | 1 | P99Micros | 1087.4 | 354.4 | 3.07× |
| 29 | api | small | 1 | P50Micros | 475.4 | 163.0 | 2.92× |
| 30 | api | medium | 1 | P50Micros | 528.3 | 191.3 | 2.76× |
| 31 | wire | medium | 16 | OpsPerSecond | 31653.0 | 77359.9 | 2.44× |
| 32 | wire | medium | 1 | P99Micros | 786.2 | 411.3 | 1.91× |
| 33 | wire | small | 16 | OpsPerSecond | 70814.2 | 116605.6 | 1.65× |
| 34 | wire | small | 1 | P99Micros | 557.4 | 354.4 | 1.57× |
| 35 | wire | medium | 1 | P50Micros | 206.8 | 191.3 | 1.08× |
| 36 | wire | small | 1 | P50Micros | 134.1 | 163.0 | 0.82× |

## All local cells

Percentiles are medians of per-repeat percentiles, not pooled percentiles. Batch throughput counts requests; each batch contains 16 documents. These are closed-loop measurements.

| Size | Operation | c | Lane | ops/s median (min–max) | p50 µs | p99 µs | Samples | Errors |
| --- | --- | ---: | --- | ---: | ---: | ---: | ---: | ---: |
| small | read | 1 | turso | 11099 (10308–14188) | 82.1 | 147.0 | 177970 | 0 |
| small | read | 1 | turso-cdc | 13663 (7901–14074) | 67.9 | 103.0 | 178194 | 0 |
| small | read | 1 | engine | 259562 (249358–272819) | 2.1 | 9.9 | 3908700 | 0 |
| small | read | 1 | api | 1994 (1878–2012) | 475.4 | 1087.4 | 29420 | 0 |
| small | read | 1 | api-query | 775 (763–804) | 1365.8 | 3741.8 | 11705 | 0 |
| small | read | 1 | api-batch | 341 (314–375) | 3000.9 | 5867.8 | 5153 | 0 |
| small | read | 1 | sql | 2 (2–2) | 410999.2 | 486938.8 | 208 | 0 |
| small | read | 1 | hrana | 2 (2–2) | 409926.1 | 459496.8 | 209 | 0 |
| small | read | 1 | pg | 2 (2–2) | 418549.6 | 660782.6 | 203 | 0 |
| small | read | 1 | wire | 6397 (5645–6446) | 134.1 | 557.4 | 92439 | 0 |
| small | read | 1 | tcp-echo | 12611 (10723–14478) | 66.1 | 175.2 | 189058 | 0 |
| small | read | 16 | turso | 151192 (138528–152726) | 100.9 | 151.4 | 2212232 | 0 |
| small | read | 16 | turso-cdc | 145449 (132246–153521) | 101.7 | 154.8 | 2156080 | 0 |
| small | read | 16 | engine | 570941 (549234–650347) | 4.1 | 790.1 | 8852608 | 0 |
| small | read | 16 | api | 21057 (17920–21751) | 511.5 | 4930.1 | 303643 | 0 |
| small | read | 16 | api-query | 14069 (11113–14628) | 627.1 | 7271.8 | 199046 | 0 |
| small | read | 16 | api-batch | 1442 (933–1650) | 14152.4 | 30187.5 | 20124 | 0 |
| small | read | 16 | sql | 4 (3–4) | 3158409.3 | 5767517.8 | 347 | 0 |
| small | read | 16 | hrana | 4 (4–5) | 3114193.9 | 5265059.0 | 386 | 0 |
| small | read | 16 | pg | 4 (4–5) | 3110139.4 | 6111910.2 | 404 | 0 |
| small | read | 16 | wire | 70814 (57112–78961) | 165.2 | 2334.8 | 1034435 | 0 |
| small | read | 16 | tcp-echo | 129562 (125277–159504) | 84.7 | 512.4 | 2071714 | 0 |
| small | write | 1 | turso | 1026 (813–1065) | 1119.7 | 2559.0 | 14517 | 0 |
| small | write | 1 | turso-cdc | 732 (730–806) | 1259.7 | 2997.0 | 11342 | 0 |
| small | write | 1 | engine | 193 (189–195) | 4663.9 | 15629.4 | 2882 | 0 |
| small | write | 1 | api | 179 (161–180) | 5249.9 | 14479.2 | 2599 | 0 |
| small | write | 1 | api-query | 174 (145–184) | 5381.3 | 14282.6 | 2516 | 0 |
| small | write | 1 | api-batch | 63 (51–64) | 14910.1 | 26852.2 | 887 | 0 |
| small | write | 1 | sql | 140 (105–148) | 6714.4 | 16359.2 | 1962 | 0 |
| small | write | 1 | hrana | 137 (108–146) | 6521.4 | 15514.6 | 1955 | 0 |
| small | write | 1 | pg | 156 (113–162) | 5985.6 | 14527.9 | 2156 | 0 |
| small | write | 1 | wire | 193 (136–196) | 4750.8 | 14330.0 | 2622 | 0 |
| small | write | 1 | tcp-echo | 10941 (10220–13751) | 73.3 | 201.7 | 174562 | 0 |
| small | write | 16 | turso | 963 (822–1011) | 15835.4 | 32174.0 | 13976 | 0 |
| small | write | 16 | turso-cdc | 744 (721–768) | 20175.5 | 39465.9 | 11164 | 0 |
| small | write | 16 | engine | 700 (611–723) | 21279.6 | 46221.9 | 10170 | 0 |
| small | write | 16 | api | 698 (568–701) | 21517.1 | 46304.6 | 9834 | 0 |
| small | write | 16 | api-query | 675 (629–684) | 21058.2 | 44087.8 | 9945 | 0 |
| small | write | 16 | api-batch | 86 (83–86) | 178566.8 | 222543.1 | 1279 | 0 |
| small | write | 16 | sql | 281 (280–282) | 54285.0 | 84806.0 | 4217 | 0 |
| small | write | 16 | hrana | 275 (237–299) | 55947.3 | 80221.6 | 4055 | 0 |
| small | write | 16 | pg | 291 (233–292) | 51492.6 | 123875.7 | 4075 | 0 |
| small | write | 16 | wire | 675 (560–675) | 21818.2 | 55450.5 | 9550 | 0 |
| small | write | 16 | tcp-echo | 135632 (110826–138186) | 85.8 | 1113.4 | 1923220 | 0 |
| medium | read | 1 | turso | 8676 (7091–10295) | 108.4 | 188.5 | 130315 | 0 |
| medium | read | 1 | turso-cdc | 10254 (6505–10455) | 91.3 | 143.8 | 136071 | 0 |
| medium | read | 1 | engine | 112785 (55908–122656) | 4.1 | 243.6 | 1456748 | 0 |
| medium | read | 1 | api | 1699 (1463–1830) | 528.3 | 2062.0 | 24965 | 0 |
| medium | read | 1 | api-query | 715 (563–732) | 1455.9 | 3640.2 | 10048 | 0 |
| medium | read | 1 | api-batch | 270 (166–271) | 3606.6 | 9882.6 | 3541 | 0 |
| medium | read | 1 | sql | 2 (2–2) | 403928.1 | 487660.0 | 212 | 0 |
| medium | read | 1 | hrana | 2 (2–2) | 410768.6 | 545090.3 | 209 | 0 |
| medium | read | 1 | pg | 2 (2–2) | 413114.2 | 706623.7 | 213 | 0 |
| medium | read | 1 | wire | 4154 (3947–4791) | 206.8 | 786.2 | 64459 | 0 |
| medium | read | 1 | tcp-echo | 12017 (10962–15880) | 65.5 | 185.4 | 194292 | 0 |
| medium | read | 16 | turso | 85955 (85624–88237) | 138.4 | 1733.5 | 1299079 | 0 |
| medium | read | 16 | turso-cdc | 84450 (77603–89694) | 140.0 | 1823.1 | 1258737 | 0 |
| medium | read | 16 | engine | 220774 (179234–234760) | 7.5 | 3087.8 | 3173843 | 0 |
| medium | read | 16 | api | 15002 (14913–16327) | 601.0 | 6097.0 | 231212 | 0 |
| medium | read | 16 | api-query | 10492 (10101–11017) | 699.7 | 9074.7 | 158049 | 0 |
| medium | read | 16 | api-batch | 657 (584–707) | 29079.6 | 53962.1 | 9742 | 0 |
| medium | read | 16 | sql | 4 (4–4) | 3073589.2 | 5726861.7 | 365 | 0 |
| medium | read | 16 | hrana | 4 (4–4) | 3069750.5 | 5619098.9 | 391 | 0 |
| medium | read | 16 | pg | 4 (3–4) | 3919049.9 | 8478633.0 | 327 | 0 |
| medium | read | 16 | wire | 31653 (27862–41608) | 268.2 | 4303.4 | 505612 | 0 |
| medium | read | 16 | tcp-echo | 121919 (105799–141026) | 96.9 | 839.5 | 1843720 | 0 |
| medium | write | 1 | turso | 835 (732–845) | 1248.5 | 4187.9 | 12062 | 0 |
| medium | write | 1 | turso-cdc | 603 (597–706) | 1406.2 | 5226.7 | 9525 | 0 |
| medium | write | 1 | engine | 165 (161–191) | 5443.1 | 19982.4 | 2584 | 0 |
| medium | write | 1 | api | 134 (125–172) | 6855.2 | 18093.3 | 2153 | 0 |
| medium | write | 1 | api-query | 147 (144–172) | 6170.9 | 17145.4 | 2314 | 0 |
| medium | write | 1 | api-batch | 46 (43–47) | 20567.1 | 41773.8 | 679 | 0 |
| medium | write | 1 | sql | 127 (121–139) | 7078.6 | 20605.4 | 1932 | 0 |
| medium | write | 1 | hrana | 125 (112–136) | 7250.6 | 20259.8 | 1865 | 0 |
| medium | write | 1 | pg | 148 (124–159) | 6061.1 | 15835.1 | 2154 | 0 |
| medium | write | 1 | wire | 185 (152–187) | 4986.1 | 15628.8 | 2617 | 0 |
| medium | write | 1 | tcp-echo | 8267 (8181–10287) | 95.0 | 264.3 | 133675 | 0 |
| medium | write | 16 | turso | 922 (717–945) | 15762.3 | 43456.6 | 12919 | 0 |
| medium | write | 16 | turso-cdc | 665 (661–689) | 22012.0 | 42466.9 | 10072 | 0 |
| medium | write | 16 | engine | 579 (520–598) | 24377.0 | 59044.3 | 8484 | 0 |
| medium | write | 16 | api | 588 (477–596) | 25599.5 | 50357.4 | 8304 | 0 |
| medium | write | 16 | api-query | 566 (454–573) | 26221.9 | 57872.9 | 7966 | 0 |
| medium | write | 16 | api-batch | 61 (55–61) | 250784.5 | 321551.5 | 881 | 0 |
| medium | write | 16 | sql | 251 (229–262) | 60199.2 | 84261.1 | 3710 | 0 |
| medium | write | 16 | hrana | 254 (243–258) | 60002.9 | 87224.4 | 3777 | 0 |
| medium | write | 16 | pg | 275 (262–283) | 55994.5 | 72777.6 | 4098 | 0 |
| medium | write | 16 | wire | 592 (579–594) | 24642.9 | 53900.4 | 8827 | 0 |
| medium | write | 16 | tcp-echo | 59993 (57741–62851) | 125.6 | 1896.7 | 902925 | 0 |
