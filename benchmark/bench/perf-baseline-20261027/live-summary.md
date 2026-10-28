# Live regional baseline

Serving revision(s): 64fdad763ce4d777e665dd606cdf7e9672d0deca.
Harness provenance is separate: older target_sha fields are retained as legacy_operator_source_hint, not serving revisions.
Evidence complete; observed production load is recorded in metadata. Reads are c=1, paced at most 10/s; percentiles pool 180 samples. These are host-local endpoint latencies, not public WAN latencies.
Nearest-rank pooled p99 is the second-largest of 180 samples; each 60-sample repeat's p99 equals its maximum. These are descriptive tails, not confidence bounds.

| Region | Endpoint | Size | N | p50 ms | Pooled p99 ms | Repeat maxima min–max ms |
| --- | --- | --- | ---: | ---: | ---: | --- |
| fra | batch | medium | 180 | 2.680 | 108.342 | 52.006–180.409 |
| fra | batch | small | 180 | 2.128 | 191.694 | 58.503–226.249 |
| fra | documents | medium | 180 | 1.227 | 191.753 | 26.991–503.240 |
| fra | documents | small | 180 | 1.328 | 199.863 | 139.867–426.364 |
| fra | hrana | medium | 180 | 6.394 | 202.732 | 62.666–369.562 |
| fra | hrana | small | 180 | 5.661 | 68.607 | 58.205–108.393 |
| fra | postgres | medium | 180 | 5.834 | 247.777 | 55.214–508.774 |
| fra | postgres | small | 180 | 5.619 | 282.631 | 43.585–929.749 |
| fra | query | medium | 180 | 1.382 | 54.678 | 22.666–58.590 |
| fra | query | small | 180 | 1.648 | 83.116 | 75.275–99.743 |
| fra | sql | medium | 180 | 7.808 | 181.284 | 69.280–251.636 |
| fra | sql | small | 180 | 6.274 | 356.643 | 74.529–405.027 |
| fra | wire | medium | 180 | 0.791 | 78.693 | 15.198–89.666 |
| fra | wire | small | 180 | 0.697 | 114.733 | 38.936–276.486 |
| gru | batch | medium | 180 | 3.598 | 94.998 | 90.257–100.351 |
| gru | batch | small | 180 | 2.268 | 95.242 | 77.945–107.300 |
| gru | documents | medium | 180 | 1.745 | 78.490 | 77.385–79.216 |
| gru | documents | small | 180 | 1.686 | 78.090 | 67.846–93.018 |
| gru | hrana | medium | 180 | 6.508 | 92.226 | 72.434–180.578 |
| gru | hrana | small | 180 | 8.509 | 111.063 | 91.899–182.051 |
| gru | postgres | medium | 180 | 5.869 | 79.855 | 76.326–97.981 |
| gru | postgres | small | 180 | 5.869 | 98.194 | 67.430–207.065 |
| gru | query | medium | 180 | 1.860 | 91.327 | 64.782–181.487 |
| gru | query | small | 180 | 1.727 | 88.602 | 81.338–93.448 |
| gru | sql | medium | 180 | 7.953 | 112.508 | 97.816–177.018 |
| gru | sql | small | 180 | 7.470 | 91.846 | 85.582–99.209 |
| gru | wire | medium | 180 | 1.027 | 86.687 | 71.690–177.798 |
| gru | wire | small | 180 | 1.179 | 90.832 | 80.174–98.707 |
| sjc | batch | medium | 180 | 2.583 | 203.424 | 35.758–2014.519 |
| sjc | batch | small | 180 | 2.135 | 66.766 | 51.665–86.281 |
| sjc | documents | medium | 180 | 1.387 | 77.277 | 58.541–148.137 |
| sjc | documents | small | 180 | 1.549 | 120.073 | 45.663–122.660 |
| sjc | hrana | medium | 180 | 6.630 | 172.421 | 40.127–208.290 |
| sjc | hrana | small | 180 | 8.291 | 112.143 | 49.003–160.387 |
| sjc | postgres | medium | 180 | 6.094 | 724.078 | 63.877–1019.850 |
| sjc | postgres | small | 180 | 4.844 | 193.015 | 42.291–495.866 |
| sjc | query | medium | 180 | 1.576 | 53.367 | 48.362–54.409 |
| sjc | query | small | 180 | 1.634 | 102.941 | 48.924–107.727 |
| sjc | sql | medium | 180 | 7.250 | 202.098 | 71.552–2117.004 |
| sjc | sql | small | 180 | 8.086 | 578.289 | 69.871–1534.892 |
| sjc | wire | medium | 180 | 0.886 | 59.742 | 21.434–170.009 |
| sjc | wire | small | 180 | 0.769 | 98.732 | 11.337–471.787 |

| Direction | N | Floor ms | p50 ms | p99 ms | Missing | Target p99 <200 ms |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| fra->gru | 180 | 90.629 | 233.462 | 1597.094 | 0 | NOT MET |
| fra->sjc | 180 | 68.980 | 160.462 | 1611.384 | 0 | NOT MET |
| gru->fra | 180 | 90.666 | 229.824 | 617.958 | 0 | NOT MET |
| gru->sjc | 180 | 84.605 | 214.104 | 912.471 | 0 | NOT MET |
| sjc->fra | 180 | 68.997 | 168.582 | 1310.032 | 0 | NOT MET |
| sjc->gru | 180 | 84.547 | 214.648 | 1705.507 | 0 | NOT MET |
