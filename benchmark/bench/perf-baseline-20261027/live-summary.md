# Live regional baseline

Evidence complete; observed production load is recorded in metadata. Reads are c=1, paced at most 10/s; percentiles pool 180 samples. These are host-local endpoint latencies, not public WAN latencies.

| Region | Endpoint | Size | N | p50 ms | p99 ms | p99 per-repeat min–max ms |
| --- | --- | --- | ---: | ---: | ---: | --- |
| fra | documents | medium | 180 | 1.235 | 53.789 | 50.605–72.254 |
| fra | documents | small | 180 | 1.236 | 622.411 | 68.423–1533.944 |
| fra | hrana | medium | 180 | 5.800 | 703.675 | 44.982–2109.949 |
| fra | hrana | small | 180 | 6.080 | 77.184 | 59.404–88.286 |
| fra | postgres | medium | 180 | 3.785 | 61.371 | 38.220–70.339 |
| fra | postgres | small | 180 | 6.412 | 324.540 | 19.836–1543.726 |
| fra | sql | medium | 180 | 5.918 | 181.599 | 77.911–241.340 |
| fra | sql | small | 180 | 7.265 | 99.512 | 55.045–112.857 |
| fra | wire | medium | 180 | 0.895 | 563.783 | 56.080–2127.300 |
| fra | wire | small | 180 | 0.719 | 51.295 | 19.104–65.702 |
| gru | documents | medium | 180 | 1.872 | 96.601 | 87.656–183.409 |
| gru | documents | small | 180 | 1.662 | 95.432 | 93.883–100.656 |
| gru | hrana | medium | 180 | 10.576 | 280.639 | 107.815–407.612 |
| gru | hrana | small | 180 | 9.221 | 173.003 | 83.899–177.020 |
| gru | postgres | medium | 180 | 10.737 | 129.610 | 107.413–277.165 |
| gru | postgres | small | 180 | 7.234 | 116.136 | 87.188–284.263 |
| gru | sql | medium | 180 | 12.153 | 301.453 | 87.184–405.339 |
| gru | sql | small | 180 | 8.261 | 192.155 | 102.334–207.137 |
| gru | wire | medium | 180 | 1.089 | 87.393 | 86.060–95.161 |
| gru | wire | small | 180 | 1.035 | 90.908 | 80.894–103.224 |
| sjc | documents | medium | 180 | 1.643 | 52.137 | 41.999–55.355 |
| sjc | documents | small | 180 | 1.297 | 108.646 | 49.141–218.019 |
| sjc | hrana | medium | 180 | 5.750 | 89.571 | 52.298–178.527 |
| sjc | hrana | small | 180 | 7.395 | 324.641 | 69.678–375.764 |
| sjc | postgres | medium | 180 | 3.707 | 50.381 | 28.372–60.610 |
| sjc | postgres | small | 180 | 7.213 | 149.667 | 101.196–156.116 |
| sjc | sql | medium | 180 | 6.369 | 64.285 | 49.827–71.639 |
| sjc | sql | small | 180 | 7.920 | 210.089 | 52.783–507.106 |
| sjc | wire | medium | 180 | 0.850 | 87.747 | 28.605–92.881 |
| sjc | wire | small | 180 | 0.897 | 109.055 | 50.855–214.129 |

| Direction | N | Floor ms | p50 ms | p99 ms | Missing | Target p99 <200 ms |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| fra->gru | 180 | 90.637 | 217.108 | 1480.450 | 0 | NOT MET |
| fra->sjc | 180 | 72.089 | 127.515 | 345.612 | 0 | NOT MET |
| gru->fra | 180 | 90.702 | 202.543 | 1107.936 | 0 | NOT MET |
| gru->sjc | 180 | 84.346 | 240.135 | 2431.839 | 0 | NOT MET |
| sjc->fra | 180 | 68.704 | 131.725 | 1443.139 | 0 | NOT MET |
| sjc->gru | 180 | 84.317 | 212.437 | 798.640 | 0 | NOT MET |
