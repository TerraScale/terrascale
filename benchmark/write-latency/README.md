# Regional write latency

Create a unique Free synthetic account with `ts-throwaway-account <tag>`.
Run `run.py --tag <tag> --output <private-temporary-result.json>` on a client in
SJC. Select `--region fra` on a FRA client for the same proof there; GRU remains
the home region. The default performs three repeats with a fresh database each time, 40
measured writes per mode and transport, and five excluded warmup writes. It
verifies home row counts and deletes every database. Delete the account afterward
with `ts-throwaway-account --delete <tag>`; complete its second deletion call if
requested. Remove any private credential files copied to a serving host.

The client uses fresh HTTPS, reused HTTPS and a reused connection to the serving
node's client port (`127.0.0.1:8080`). Token renewal, serialization, setup,
warmup, validation and cleanup are outside the timed network request. Fresh
HTTPS includes connection creation and TLS. All transports fully consume the
response body before the next request.

Modes are an ordinary eligible insert, an eligible insert with
`TerraScale-Durability: home`, and an insert forced home with
`TerraScale-Write: home`. `--expect-local` requires ordinary inserts to commit
locally; without it, they must commit home. This selects a benchmark expectation
and never changes service configuration. The durability-home mode must always
commit through the local overlay and receive the home's applied acknowledgement.

`--transports` selects the measured transports, `--writes` changes the number of
samples, and `--start-repeat` gives a resumed run fresh database names. Every
sample is retained; p99 uses the rounded order-statistic index of the R7 proof
and is the observed maximum with 40 samples. These are sequential latency
measurements, not a load test.

Run on the serving host with `--snapshot-script <path-to-push-proof/metrics.py>`
to record authenticated internal health and source revisions around each
repeat. A changed revision marks that repeat `source_stable: false`; exclude it
from a comparison and retain it as qualification evidence. The script reads
credentials only from private account files; `WRITE_PROOF_ACCOUNT_ROOT` can
point to a private temporary directory. Outputs contain measurements, routes,
health and metrics, without credentials or SQL bodies.

Add `--request-logs` to correlate every measured response's `X-Request-ID`
with the exact server `latencyMs` observation. This requires `--snapshot-script`
and reads the existing authenticated internal logs endpoint on every discovered
entry-region node, including pending observations. A node with no database log
may return 404 and contributes no observations. Missing or duplicate response IDs and
missing server observations fail the proof. Request-log time includes response
completion; `app` timing ends at response headers.

Database identity is saved immediately after creation, with cleanup status saved
after deletion, so an interrupted proof retains its cleanup evidence.
Measured samples are saved before the final snapshot. A failed snapshot leaves
`snapshot_complete: false` and `source_stable: false`; retain these diagnostics
and exclude them from the final comparison.

`push-proof/metrics.py --health-samples 20` also measures direct internal HTTP/2
health for every discovered peer. Each node gets one reused connection; its
first sample includes TCP connection setup. This separates mesh RTT from the
public edge and TLS. Runtime peer credentials remain on the serving host and
reach curl only on stdin.

Health snapshots also retain `peer_local_ack_metrics` for every discovered entry-region
node. These record partner health and local-write availability alongside the
actual node identities, so a one-node acknowledgement is distinguishable from
an acknowledgement with an in-region partner.

Snapshots retain push and catalog-cache counters from every discovered peer.
Compare their deltas around each repeat to distinguish home apply retries and
attempt time from transport and to confirm catalog caches remain warm. The
acknowledgement substeps in `Server-Timing` are nested inside the total `ack`
span and must not be added to it.
