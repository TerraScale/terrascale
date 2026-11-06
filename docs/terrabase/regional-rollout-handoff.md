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
that legacy peer cannot persist protocol-2 activation. For the first
replacement of the currently deployed legacy binary, the existing legacy
handoff leaves the other old process serving; the upgraded process starts as its
follower. The surviving legacy writer can then hand off to the upgraded peer
before it is replaced. The upgraded candidate accepts the legacy preparation
payload and persists a certificate before acknowledging it. This transition still
needs a separately
approved, coordinated release window; a local drill pass grants no deployment
permission. Existing legacy processes cannot implement the new rollback protocol
until replaced, so the mixed-version transition is not counted among the three
protocol-2 acceptance rollouts. Its first handoff still lacks protocol-2 automatic
recovery and durable stop exclusion; passing the new-only drills does not qualify
those old binaries. Keep the release hold until that transition plan is approved.

Qualification requires five repeats of each critical drill: foreign lease replay;
target failure after owner CAS before admission; controller disconnection before
reservation, before CAS, after CAS and after activation; quorum partition during
rollback; both processes killed with a native write open; and manual owner CAS
while writes run. Every drill reconciles acknowledged rows. Three later live
rollouts must separately meet the request-error, latency and bounded-recovery
criteria under the agreed regional load.
