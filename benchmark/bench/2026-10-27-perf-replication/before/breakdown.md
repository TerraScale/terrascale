| Direction | Mode | n | origin server p50/p99 | commit->visible minus one-way p50/p99 | slow probes (>200ms): share where origin server > half |
|---|---|---:|---|---|---|
| fra-gru | idle | 180 | 20/250 | 83/405 | 98 slow, 6% origin-bound |
| fra-sjc | idle | 180 | 21/884 | 31/728 | 28 slow, 39% origin-bound |
| gru-fra | idle | 180 | 81/365 | 27/319 | 98 slow, 35% origin-bound |
| gru-sjc | idle | 180 | 75/445 | 34/352 | 99 slow, 35% origin-bound |
| sjc-fra | idle | 180 | 26/473 | 35/814 | 52 slow, 48% origin-bound |
| sjc-gru | idle | 180 | 25/261 | 58/281 | 91 slow, 8% origin-bound |
| fra-gru | load | 180 | 24/322 | 82/408 | 106 slow, 9% origin-bound |
| fra-sjc | load | 180 | 22/474 | 31/266 | 59 slow, 37% origin-bound |
| gru-fra | load | 180 | 93/487 | 25/225 | 118 slow, 49% origin-bound |
| gru-sjc | load | 180 | 105/523 | 35/693 | 124 slow, 56% origin-bound |
| sjc-fra | load | 180 | 31/678 | 32/1446 | 59 slow, 41% origin-bound |
| sjc-gru | load | 180 | 25/364 | 69/380 | 115 slow, 14% origin-bound |
