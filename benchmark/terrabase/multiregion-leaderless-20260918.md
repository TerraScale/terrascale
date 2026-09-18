# TerraBase multi-region leaderless replication — 2026-09-18

Three real TerraBase member **processes** — not in-process hosts — launched
from environment configuration alone (`TerraBase.Program` member mode), each
in its own container ("region": GRU, FRA, SFO), with **40 ms ± 5 ms netem
delay applied only to member-to-member traffic** (a `prio`-band `tc` filter
matching the other members' bridge IPs; the driver's own traffic is
undelayed). The fan-out is composed by the host from a registered
`LeaderlessFanoutMembership` — nothing replication-related is
caller-constructed.

Topology: **relay ring** `member-gru → member-fra → member-sfo → member-gru`.
Each member accepts pushes only from itself and its ring predecessor and
fans out only to its ring successor, so every write is delivered
transitively: a write at GRU reaches FRA after one 40 ms hop and SFO only
through FRA's relay of the durably applied push.

Receipt: `receipts/20260918-multiregion-leaderless/` (raw JSONL, driver log,
member log, orchestration script, Containerfile).

## What it proves

| Property | Evidence |
| --- | --- |
| Ack is local despite 40 ms inter-region RTT | write ack p50 ≈ 18 ms at GRU / ≈ 21 ms at FRA — the WAL `fdatasync` cost, not a cross-region round trip |
| Transitive leaderless delivery | GRU-written dots visible at SFO (2 hops) ≈ 60–70 ms after FRA (1 hop) — one more netem hop plus one relay apply |
| A down region never blocks writes | with SFO's container stopped, a 128-record write at GRU still acked in 13.8 ms |
| A restarted region catches up by delta | SFO restarted and converged 6.5 s later (container + app start + 1 s retry backoff + one delta drain) |
| Convergence | all three members held the identical 3,879-dot set after both write phases |

## Numbers (15 samples per phase, batch = 128 records ≈ 250 B each)

Writes at **GRU**; visibility measured by polling the member's resumable
pull path from an undelayed observer:

| Metric | p50 | Range |
| --- | ---: | --- |
| ack at GRU (local WAL fdatasync) | 18.4 ms | 14.7–36.7 ms |
| visible at FRA (1 hop, 40 ms) | 164.8 ms | 116.5–231.2 ms |
| visible at SFO (2 hops, 80 ms) | 230.3 ms | 194.9–294.5 ms |

Writes at **FRA**:

| Metric | p50 | Range |
| --- | ---: | --- |
| ack at FRA | 21.5 ms | 17.0–26.9 ms |
| visible at SFO (1 hop) | 229.1 ms | 176.7–308.7 ms |
| visible at GRU (2 hops) | 285.4 ms | 230.1–359.1 ms |

**Visibility is an upper bound.** Each poll pages the member's whole journal
from the retained floor (the durable observer pattern; retained pull streams
are short-lived crash-resume handles), so detection lags true visibility by
up to one poll cycle (~30–80 ms as the journal grows to 3,879 records). Ack
timings are exact.

## How a member is launched

Every member runs the stock entry point with environment variables only:

```
TERRABASE_MEMBER_ENABLED=true
TERRABASE_PEER_URL=https://0.0.0.0:8443
TERRABASE_NODE_ID=member-gru            # + member-fra, member-sfo
TERRABASE_REPLICA_EPOCH=9
TERRABASE_MEMBER_CERT_FILE=/certs/member-gru.crt   # mTLS leaf
TERRABASE_MEMBER_KEY_FILE=/certs/member-gru.key
TERRABASE_CLUSTER_CA_FILE=/certs/ca.crt
TERRABASE_PEER_ALLOWLIST=[{"fingerprint":"…","memberId":"member-fra","epoch":9,
                           "url":"https://member-fra:8443", …},   # ring successor: fan-out target
                          {"fingerprint":"…","memberId":"member-sfo","epoch":9, …},  # admitted, not dialed
                          {"fingerprint":"…","memberId":"observer","epoch":9, …}]    # reader: admitted, not dialed
TERRABASE_REPLICATION_CHECKPOINT_KEY=<hex>   # signs resumable pull continuations across restarts
TERRABASE_DATABASE_*  (storage-engine backend, explicit mode, one database)
```

From this the host composes the mTLS peer listener, the per-peer
MagicOnion clients, and the Anna-style delta fan-out
(`LeaderlessFanoutHostedService`); a durably applied push relays onward
through `ILeaderlessFanout`, which is what carries GRU's writes from FRA to
SFO.

## Caveats

- One physical host: three containers on one podman bridge, WAN delay
  emulated per-direction on egress, no emulated packet loss or bandwidth
  limits, and no clock skew.
- The observer polls by full-journal pull, so visibility numbers include
  poll quantization (upper bound); ack numbers do not.
- Regions are logical (container network positions with injected delay),
  not geo-located; TLS handshakes between members also pay the 40 ms hop
  inside the first fan-out round.
- Batch=128 only; no throughput saturation, no concurrent-failure matrix.

## Reproduction

```bash
dotnet publish terrabase/.worktrees/anna/src/TerraBase/TerraBase.csproj \
  -c Release -o /tmp/multiregion-publish
cd .codex/swarm/measurement/multiregion-leaderless-20260918
SAMPLES=15 ./run-demo.sh          # builds driver, certs, containers, netem, measure
# teardown: podman rm -f member-gru member-fra member-nak; podman network rm terrabase-demo
```
