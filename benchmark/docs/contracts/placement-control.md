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

`Stage` carries `expected_revision`, a complete proposal, and the usual
`x-tb-idempotency-key`. Proposals bind the exact environment and cluster, positive
generation and replica epoch, one to three disjoint RF=2 Region seat pairs, and
an explicit writer/follower assignment for every enabled local database. Database
identities and Regions must exactly match the local configured catalog; the
proposal cannot widen residency or add a database. Seat ordering never chooses
a writer. Region seats and assignments are canonically sorted before persistence.

The first successful Stage requires expected revision zero. A replacement uses
the current local revision and strictly increases both generation and epoch.
SQLite serializes the CAS across connections. This is local ordering of pending
intent, not a distributed election. The orchestrator must persist its intent and
retry identity before contacting a member and preserve that identity after an
unknown RPC outcome. Exact replay returns the original receipt even if later
proposals exist. Reusing a retry key with another principal, expected revision,
or canonical proposal fails. `Get` returns the latest proposal or `NOT_FOUND`.

The journal retains at most 1,024 operations, each at most 256 KiB. Exhaustion
returns `RESOURCE_EXHAUSTED`; it never drops retry receipts to admit more work.
Schema versions, journal identity and retained proposal bytes are checked on
restart. Missing or corrupt storage cannot become an empty accepted membership.
Cancellation keeps dispatched disk work within the worker bound; shutdown waits
for those transactions before closing the process.

Membership acceptance and transitions remain unfinished. There is deliberately
no RPC that converts this intent into an admitted placement. A future acceptance
implementation must use the engine's durable catch-up, reconciliation and old
writer fencing proofs. It cannot infer them from this journal, NATS leases,
controller identity membership, health reports or elapsed time.
