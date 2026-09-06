# Pending placement control

`PlacementControlService` in the TBEP protobuf persists orchestrator placement
intent in an owner-private SQLite journal. This implements the control-gRPC peer
list handoff in ADR 0041. A response with `PENDING` proves only that the intent and
retry receipt committed locally with SQLite `synchronous=FULL`. It does not prove
membership acceptance, catch-up, writer fencing, replication, or readiness.
ADR 0044 sections 2.1 and 2.6 still require those facts before data service.

The production daemon enables this listener with an explicit configuration:

```toml
[control]
proposal_journal = "/var/lib/terrabase/placement/proposals.sqlite"
orchestrator_principals = ["spiffe://terrascale.tech/production/orchestrator/gru-1"]
observer_principals = ["spiffe://terrascale.tech/production/observer/gru-1"]
```

The journal parent must already exist with mode `0700`; the journal uses mode
`0600`. The listener uses the configured private TBEP address, server certificate,
private key and client CA. TLS 1.3 mutual authentication is mandatory. Every RPC
carries exactly one `x-tb-principal` matching the client certificate's sole SPIFFE
URI SAN and the appropriate control allowlist. Stage/Get reject database scope metadata.
The separate read-only observer role exposes the existing Maintenance topology,
admin and supervision observations with validated full scope metadata. Pending
observations report `Starting`, unavailable engine topology and unknown cluster
mode; shutdown reports `Draining`. Observers cannot stage intent or request
shutdown, and writer/observer allowlists must be disjoint. Either role may be
configured alone. The listener exposes no customer data operations. Its
lifecycle metrics remain in `Starting`; a healthy process or successful Stage
is not readiness.
Without this configuration the missing-membership startup refusal remains.

`Stage` carries `expected_revision`, a complete proposal, optional provisioning
declarations, and the usual
`x-tb-idempotency-key`. Proposals bind the exact environment and cluster, positive
generation and replica epoch, one to three disjoint RF=2 Region seat pairs, and
an explicit regional writer/follower assignment for every declared database.
Each database's assignment Regions are its selected residency set: a database
may use only one Region while the proposal carries seats for several Regions.
Every additional selected Region carries its own writer/follower pair, and the
pair must use that Region's authenticated seats. The control catalog includes
databases stored elsewhere; their declarations grant no local storage access.
Seat ordering never chooses a writer. Region seats and
assignments are canonically sorted before persistence. Retained accepted
proposals remain valid when a later local database is registered; the later
proposal must include that database before it can be staged.

Provisioning declarations carry the exact tenant, project, database, and
selected Regions from the orchestrator's durable admission records. The member
requires those Regions to match the represented proposal assignments exactly.
Existing identity and selected residency cannot change. Unknown declarations
are committed with the proposal CAS as control metadata only. A failed CAS or
conflicting retry leaves no declaration behind. Known declarations are redundant
with the proposal's owner/residency sets and may be omitted from the request.

Local storage admission is separate. The declaration journal stores an empty
`declared.region` for control-only scopes, and a registered Region for local
admissions. Only the authenticated storage-admission callback can promote a
control-only scope to a local admission. Configuration and Stage cannot make
that promotion implicitly. WAL assignment lookup rejects a control-only scope
even after its global placement is accepted. Restart retains local admissions
recorded after the original configuration was loaded.

The first successful Stage requires expected revision zero. A replacement uses
the current local revision and strictly increases both generation and epoch.
SQLite serializes the CAS across connections. Staging and local promotion reload
the durable catalog in an `IMMEDIATE` transaction before validation; stale
process caches cannot overwrite another connection's admission. This is local ordering of pending
intent, not a distributed election. The orchestrator must persist its intent and
retry identity before contacting a member and preserve that identity after an
unknown RPC outcome. Exact replay returns the original receipt even if later
proposals exist. Reusing a retry key with another principal, expected revision,
or canonical proposal fails. `Get` returns the latest proposal or `NOT_FOUND`.

The journal retains at most 1,024 operations, each proposal at most 256 KiB.
Stage requests including declarations are bounded to 512 KiB and the configured
transport limit, whichever is smaller. Exhaustion
returns `RESOURCE_EXHAUSTED`; it never drops retry receipts to admit more work.
Schema versions, journal identity and retained proposal bytes are checked on
restart. Missing or corrupt storage cannot become an empty accepted membership.
Cancellation keeps dispatched disk work within the worker bound; shutdown waits
for those transactions before closing the process.

`Accept` verifies the signed transition against its configured trust pins,
the exact staged proposal, the retained predecessor, and required old-pair
fencing receipts. Accepted proofs survive restart. This remains distinct from
data-plane readiness, which requires actual catch-up, reconciliation, and
durability evidence. Neither staging nor an orchestrator lease establishes
those facts. Complete production runtime composition remains unfinished.
