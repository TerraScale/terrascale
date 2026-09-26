# HTTPS document point read: https-doc-20260926-01

| Workload | Status | Attempted | Correct | Failed | Idle delay (ms) | Offer cap (req/s) | p50 (ms) | p95 (ms) | p99 (ms) | Max (ms) |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1,000 serial authenticated GETs | passed | 1000 | 1000 | 0 | 20 | 50.0 | 3.371 | 5.005 | 7.229 | 30.738 |

Each sample uses a new HTTPS connection and verifies the entire parsed JSON response. The untimed preflight passed. A minimum 20 ms idle delay follows each timed attempt except the last; the configured offer is at most 50.0 requests/s, and actual offered load may be lower. Latency starts immediately before request send and includes TLS, server authentication, HTTP, body read, JSON parsing, and validation. Client and request construction, pacing, and sample writes are outside that timer.

[Receipt](receipt.json) · [Raw samples](samples.jsonl)
