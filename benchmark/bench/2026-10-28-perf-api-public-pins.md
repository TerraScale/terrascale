# Public API pin and protocol observation, 2026-10-28

At 10:18:58–10:18:59 UTC, six read-only requests from delta to `https://api.terrascale.tech/v1/regions` passed:

| Requested pin | HTTP/1.1 | HTTP/2 | Serving-region header | Catalog |
|---|---|---|---|---|
| fra | 200, HTTP/1.1 | 200, HTTP/2 | Exactly one `fra` | fra, gru, sjc |
| gru | 200, HTTP/1.1 | 200, HTTP/2 | Exactly one `gru` | fra, gru, sjc |
| sjc | 200, HTTP/1.1 | 200, HTTP/2 | Exactly one `sjc` | fra, gru, sjc |

The [script](../scripts/api-public-pins-20261028.py) discovers and validates every region in the admitted ts-orch peers snapshot, then requests both client protocols with `X-TerraScale-Region-Pin`. Curl 8.22.0 uses normal TLS verification, no credentials and no redirect-follow option. A fresh curl process is used for each observation; HTTP/2 negotiation is checked rather than assumed. These observations do not count internal wire replays or determine the edge-to-server protocol.

Command: `nice -n 19 python3 benchmark/scripts/api-public-pins-20261028.py /tmp/perf-api-admitted-peers-20261028-0915.txt benchmark/bench/2026-10-28-perf-api-public-pins.jsonl`. Peers snapshot SHA-256: `3cd3cdd8a4f8f8cba6ed01009c56061be06a0f3468e80c25b48309a4e406067c` (admitted at 09:17:54 UTC). Use a fresh admitted peers snapshot and new output path for another observation.

Script SHA-256: `6cba504c792d727590e18d29d69cbeb2336e070c505cc7fe8448ef453a36eb73`. [Allowlisted raw observations](2026-10-28-perf-api-public-pins.jsonl) SHA-256: `99b3be324a7d23ae4422cc941574ee7ae25e597d480747041155cae6b38ac9c4`.

This verifies the public path separately from peer-authenticated health. One request per combination establishes only this observation, not ongoing availability, authenticated document correctness, SDK transport behavior, latency or throughput. No tenant or write was used. The original server-over-engine target remains unmet.
