# Regional rollout authority and recovery

Each region has one writer and a serving peer that follows applied data and
retains its independent R10 acknowledgement holds. Global per-database home
leases apply only to cross-region replicated databases. Regional-only platform
databases share the regional writer's authority and ignore legacy global keys.

The region's durable owner record is in `terrabase-region-owners/<region>`.
Protocol 2 adds an activation marker and a bounded transfer record. A second
stream, `terrabase-region-writers/<region>`, contains exclusive admission for a
node, epoch and process session. Its server TTL is six seconds; local admission
expires five seconds after the renewal was sent, using a monotonic clock. A late
reply never extends admission. A new epoch cannot acquire that key while the
previous admission still exists. Planned transfer releases it only after ingress
and native writers have drained.

The durable phases are:

Before the engine reservation, the edge primes bulk state on the peer, then
withdraws ingress from the source and drains in-flight responses.

1. `preparing`: CAS-reserve one regional drain, identifying source, target and
   source process session. Another controller cannot drain the other node.
   Finish draining engine ingress,
   fence the source engine, sample final positions and prove the peer applied
   every position before importing/promoting its local primary files.
2. The candidate persists a preparation certificate bound to the source epoch
   and operation. An owner CAS alone cannot authorize an unprepared copy. Abort
   advances the source epoch so a cancelled preparation cannot be replayed.
3. `activating`: release the source's drained admission and CAS ownership to the
   prepared target. The target acquires exclusive admission, confirms its real
   replicated database leases and CAS-publishes activation before serving.
   That activation CAS races the rollback CAS; only one can win.
4. `retiring`: the activated target serves while the source finishes remote
   route observation and direct-request drain. A successful response permits
   stopping only the recorded source process. This phase survives controller
   death and the source's drain response. A second stop remains blocked until
   the source restarts with a different session, passes readiness and catches
   up from the current writer.
5. `recovering`: if activation never committed, CAS back to the retained source
   at a new epoch. It reacquires exclusive admission and any partially moved
   database leases. Once admitted, it clears recovery and resumes replication.

Target activation has a ten-second deadline. Each activation attempt is bounded
at two seconds, a drain at thirty seconds, and an immediate rollback attempt at
five seconds. Database-lease confirmation keeps its cursor between bounded
attempts within an epoch; a slow scan does not restart at its first database.
Failed rollback attempts leave the durable phase intact; refresh
retries after quorum returns. With reachable quorum and a healthy retained
source, a target that fails after the owner CAS before acquiring admission must
restore a writer within **30 seconds of that CAS**. The budget covers activation,
old admission expiry and a retry. No finite availability bound is claimed during
a quorum outage; writes remain fenced while quorum cannot confirm authority.

A foreign-held replicated database with a matching tenant and newer placement
moves only that database's routing and fence. It does not block all other
regional databases from activating. Source primary files remain retained during
this decision. Platform IDs and known regional-only placements never participate
in these global lease operations.

New controllers refuse to promote a peer that lacks a process-session status:
that legacy peer cannot persist protocol-2 activation. Legacy writers do not
consult the new exclusive lease at all. An empty lease key therefore does not
fence an old process, and an ordinary mixed-version handoff is unsafe.

The approved bootstrap design replaces the follower first, then restarts the
same pinned owner in place with unchanged node, epoch and retained primary data.
The executor must physically fence the old owner before starting its replacement.
The regional maintenance target is 15–30 seconds, with a 60-second recovery
budget per region, in SJC → FRA → GRU order. This requires a qualified scoped
executor driver, independent review and final release GO. No local drill grants
production permission. The bootstrap maintenance window does not count toward
zero-downtime acceptance.

`Version: 0` denotes an owner record written by a pre-protocol-2 process, rather
than protocol-2 admission or preparation. At startup, only the node already named
in that exact legacy owner record may adopt its retained primary files. Adoption
is allowed once, while the region's protocol marker is absent; it publishes the
protocol-2 marker and durable activation before admitting writes. A later
Version:0 manual CAS without a valid preparation certificate is deliberately
refused and is not automatically interpreted as a recoverable transfer. It needs
operator diagnosis; do not write legacy owner records after bootstrap.

A conflicting per-database home lease fences that database only. Activation
continues for healthy databases. Confirmation and automatic claims cannot
silently overwrite the isolated revision. After a failed owner CAS, persisted
recovery retries admission; a dead source is not selected while a certified live
target can finish activation. The activation marker and recovery CAS race on the
same revision, so recovery cannot undo a writer that acknowledged requests.

Qualification requires five repeats of each critical drill: foreign lease replay;
target failure after owner CAS before admission; controller disconnection before
reservation, before CAS, after CAS and after activation; quorum partition during
rollback; both processes killed with a native write open; and manual owner CAS
with a certified target and connected source holding admission; target failure
after admission acquisition; foreign/third-node DB isolation; dead-source recovery;
and retained Version:0 bootstrap. Every drill reconciles acknowledged rows. The
drills that run an actual b443 process (retained-owner bootstrap and v2 rollback)
were one-time local qualification on a dev host; the five-repeat evidence is
recorded under `benchmark/bench/`. Three later live
rollouts must separately meet the request-error, latency and bounded-recovery
criteria under the agreed regional load.

A conflicting per-database holder is isolated at its exact KV revision, even if
that holder is dead. Holder death does not establish tenant identity or certify
a copy. The DB stays fenced until a valid newer authoritative lease/catalog
revision is observed; unrelated DBs and the regional writer recover normally.
Same-region node expiry cannot run cross-region failover: only the certified
regional handoff protocol may change the regional writer.

Version:0 never triggers automatic rollback or promotion, including after the
pinned owner's upgrade writes its preparation certificate. A legacy process
admits without the exclusive lease, so that certificate is not authority to
reclaim a different node. Complete bootstrap on the same retained owner after
physical old-process exit; protocol-v2 transfer phases enable automatic recovery.

## Requests that reach the serving peer

The serving peer admits client-listener and management requests but does not run
them: it sends each one to the region's writer on the writer's internal listener
with the peer credential, and answers 503 while no writer is known.

- **REST.** A request that arrived on the client listener carries
  `X-TerraBase-Forwarded-For`, so the writer serves it as a client request: the
  same token, IP-allowlist, scope and quota checks, once, at the writer. One that
  arrived on the internal listener keeps its peer identity. A writer that cannot
  be reached answers `421 wrong_region`; a write it was sent but never answered
  is `503 write_outcome_unknown`
  ([unanswered forwards](sql/write-routing.md#unanswered-forwards)).
- **WebSockets** (`sql/ws`, `shape/ws`). An HTTP/1.1 Upgrade on the client
  listener or an RFC 8441 extended CONNECT on the internal listener cannot be
  forwarded as a plain request, so the peer relays the whole socket: it opens a
  WebSocket to the writer over HTTP/2 extended CONNECT with the client's
  subprotocols and headers (plus `X-TerraBase-Forwarded-For` for a client-listener
  socket), then accepts the client and pumps frames both ways for the socket's
  life. The writer runs the session exactly like one that reached it directly,
  so the 5-second transaction limit and single-writer semantics hold. A writer
  that cannot be reached answers `421 wrong_region`.


A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
