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

Each node refreshes its own region before starting observation of other regions.
Remote observations run independently of local renewal, with separate failure
boundaries and a two-second bound for each region. An invalid, missing,
unreadable or timed-out remote owner record leaves that region's last observed
owner intact and does not delay local renewal. Observation is sequential: a slow
region can delay every later region by up to its two-second attempt budget.
Regions already observed are still checked when all their peer lines disappear.
The observer uses its own NATS connection and never takes the renewal semaphore.
Connection and bucket initialization happen before admission; each successfully
created store is cached. Failed setup is attempted again on the next pass, while
renewal of an already-held lease never repeats bucket setup. Both loops receive
the service shutdown token and finish before their connections are disposed.
The read-only observer also exits when the peers file no longer admits this node.
This change removes the dependency of a node's own renewal on other regions'
records; it does not remove the dependency on the replicated store's quorum
across regions. This independence covers lease renewal and ordinary reads and
writes while admission is valid. Handoff still waits for rollout status from
every observer. One unreachable region delays drain completion until the drain
deadline, including when the new writer has already activated.

With three voting regions, losing one leaves the other two able to confirm
authority, provided they can reach each other and retain their healthy regional
peers. Reads and writes that those regions can serve locally continue; requests,
replication and operations that need the unreachable region can fail or wait.
Handoff cannot finish its observer phase while that region remains unreachable.
Losing two voting regions removes the replicated store's quorum: ownership
updates, lease renewal and handoff cannot complete. Existing regional admission
expires no later than five seconds after its last renewal was sent. New ordinary
client and management requests, including reads, can no longer be admitted for
local execution by that writer.
Internal probes and retained replica data do not establish serving authority.
Recovery waits for quorum; clock trust and local acknowledgement requirements
remain additional conditions for writes.

Ownership failures identify the own-renewal or remote-observation phase and the
region. Causes distinguish unavailable storage, timeout, an absent record, an
invalid record with its byte length, and an owner outside admitted peers with
that owner name after node-id validation. A malformed owner logs only its
length. Record contents are never logged. A changed remote failure
state is logged once; recovery logs once when observation succeeds again. The counter
`terrabase_ownership_refresh_errors_total{region}` counts failed remote refresh
attempts. Its only label is an admitted region name from the peers file or a
previous observation; it has a dedicated telemetry view with room for region
series. Repeated failures continue to increment the counter without repeating
the warning every refresh. Local invalid records and failed local renewals keep
the existing lease expiry and fencing behavior.

Retained remote owners are routing hints. A stale hint can delay forwarding,
replica synchronization, placement discovery or a home move until observation
recovers; it cannot grant writer admission. The destination must prove its own
exclusive admission and database authority before acknowledging a write.
Placement deletion treats a failed remote observation as unknown and cannot use
its retained owner to authorize forgetting data after a `404`. A changed source
endpoint can replace a follower only when the old and new source owners are
confirmed and the new source is activated; otherwise its local replica stays.
Checksum healing also waits for confirmation that its source is the activated owner.
Home lookups cache a result for up to 30 seconds. On the next lookup, a confirmed
owner whose node name differs from the cached owner causes the cached result to
be rejected. This comparison does not detect an epoch change alone or an owner
that changed and changed back between lookups. A stale route can cause forwarding,
refusal or failure; it cannot admit a second writer. Removed-placement refreshes
can remain cached for up to ten minutes after an observation changes.

Handoff import accepts database-erasure receipts and project-closure
receipts only when their supported typed formats bind to the key's database or
project, the key's holder, and the matching authority record in the incoming
snapshot. Missing or ambiguous authority, malformed records, unsupported receipt
versions and failed `Binds` or `Confirms`/`Refuses` binding checks reject the whole
import. Purge receipts use format 1; closure formats 1 and 2 require evidence
version 2. A valid closure refusal can be retained at an absent key; differing
affirmative and negative evidence at an existing key is a conflict.

A receipt absent at the receiver is stored byte for byte. An identical receipt
already there requires no write. Any different bytes at an existing receipt key
are a conflict, including an older closure receipt or two otherwise legitimate
observations at different times. Import never replaces or removes retained
receipt evidence. Conflicting duplicate receipt keys in one snapshot are also
refused. Validation, comparison and all catalog changes share one durable
transaction; invalid or conflicting receipts reject it before any snapshot row is
written and before ownership transfers. The source's existing refusal/recovery
path retains its role and resumes serving once admission is restored.

The counter `terrabase_regional_catalog_receipt_conflicts_total{kind}` counts
invalid or conflicting records, with only `database_purge_receipts` and
`project_closure_receipts` as label values. The warning is
`Regional catalog import refused: {Kind} has {Count} invalid or conflicting receipt records; catalog unchanged`.
The import error also names each receipt family and its record count; none of
these diagnostics include record contents. Operators should retain both copies
for investigation. The two safe resolutions are to cancel the handoff and keep
the source serving, or to establish the authoritative deletion evidence and make
the sender provide valid, byte-identical evidence for each retained receiver key
before attempting the handoff again. Do not delete receiver evidence to unblock
a transfer. A future sender that permits superseding receipts needs an explicit
authority and evidence ordering rule, including how to handle equal authority
with different outcomes; this importer does not infer that order.

Receipts count against the same 10,000-row and 4 MiB snapshot limits. No shipped
build exports receipts yet, so a receipt conflict cannot occur in a normal fleet
today. Receivers must accept them throughout the fleet before a later change
enables sending. Once sending exists, a conflict means two nodes disagree about
deletion evidence and requires operator investigation. A closure that needs the
previous writer's receipt still waits after a writer change, as before. The
fault journey returns ownership to the original receipt holder and verifies the
recovered stack before closure.

The crash-recovery journey requires a read-only authenticated request at the
restarted writer to answer 200 before checking its recovered data. After
restoring the foreign topology, it makes the same readiness wait at each restarted
foreign node before checking regional credentials. These waits use the existing
step timeout; they do not change bearer authentication behavior.

The durable phases are:

Before the engine reservation, the edge primes bulk state on the peer, then
withdraws ingress from the source and drains in-flight responses.

1. `preparing`: CAS-reserve one regional drain, identifying source, target and
   source process session. Another controller cannot drain the other node.
   Finish draining engine ingress,
   fence the source engine, sample final positions and prove the peer applied
   every position before importing/promoting its local primary files. A peer
   behind a sampled position pulls it from the fenced source; until its drain
   call ends, the source answers those pulls without recording the puller's
   CDC acknowledgement, so it commits nothing after the positions it certified.
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
   up from the current writer. While blocked, the drain answers HTTP 503 with
   `Retry-After: 1` and the standard error body code `source_retiring`; its message
   names the pending restart with a different session, readiness or catch-up step.
   Each refusal logs the source region, source session and pending step once, at
   information level when the step changes and debug level on repeats.
5. `recovering`: if activation never committed, CAS back to the retained source
   at a new epoch. It reacquires exclusive admission and any partially moved
   database leases. Once admitted, it clears recovery and resumes replication.

Drain transport failures return HTTP 503, `Retry-After: 1` and the standard error
body code `target_unreachable` for target readiness, preparation, verification or
activation, `observer_unreachable` for route observation after the owner CAS, or
`drain_unavailable` for admitting, reserving, collecting, priming or quiescing.
The message names the phase and peer; the warning includes the exception. A
non-caller deadline returns `drain_timeout` with the same phase and peer context.
Caller cancellation propagates. Retirement probe failures log the exception type
at debug level and retain the pending step.

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

A SQL socket holds the regional ingress lease only while it is accepted and
while the statements of one of its frames run, not while their answer is sent; a
relaying peer releases its lease once the relayed socket is accepted. An open
socket therefore does not hold up a drain, not even one whose client stops
reading an answer, though a write transaction it keeps open still delays the
drain's replica pulls by up to the 5-second transaction limit. When the source
writer's ingress hold starts, it closes every SQL socket it serves with 1012
(service restart) once the frame in flight is answered, and refuses frames that
arrive later, so no statement runs on the old writer after the fence and clients
reconnect to the new writer. A closed socket still sends every answer queued
before its 1012 and ends when the client answers the close. A client that keeps sending after the 1012 instead of answering it gets no answer to those frames and stays connected until the node stops; close the socket when you read a 1012. A client still
receiving an answer when the rollout stops the source sees the connection drop
(1006) without the 1012 and cannot tell whether that frame committed. While the
hold lasts, and on a node that was drained for a handoff (the old writer after
the hold, or a follower drained for retirement) until that node stops, a SQL socket upgrade is answered 503 with `Retry-After: 1` at
once, instead of waiting for the hold to end and being relayed through the
source, which the rollout stops next; REST requests still wait for the hold and
are then relayed to the new writer. A serving peer that was not drained keeps
relaying upgrades to the writer.


A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
