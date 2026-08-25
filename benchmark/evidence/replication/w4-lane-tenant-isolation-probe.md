# W4-REPLICATION R-3 lane receipt: tenant-isolation-probe

No production claim: lane-scoped facts from the deterministic in-process
qualification lane only.

- Commit: `f70a5d8` (branch `fleet/w4-replication`)
- Producing test: `lane_tenant_isolation_probe_denies_cross_tenant_and_is_observable`
- Run command: `cargo test --offline --locked --lib lane_tenant_isolation -- --nocapture`

## Scenario

`m-a1` writes one document for `tenant-a` carrying a canary value. A hostile
requester (`m-b1`) sends a catch-up request naming `tenant-a` while the
serving scope names `tenant-b`. The positive control then replicates the
same history under matching scopes.

## Facts asserted

- The serve seam denies the attempt with the typed
  `MemberError::CrossTenant { scope: tenant-b, attempted: tenant-a }`.
- The denial is observable: the serving member's bounded denial log records
  exactly one entry naming both tenants and the counterparty.
- Nothing crosses: no catch-up batch is received for the denied tenant.
- Positive control: the same exchange under matching tenant scopes
  replicates, and the write is never visible under `tenant-b`.

## Measurements

Not measured in this scenario: RPO, RTO, convergence interval, and
replication lag — none is the subject of the probe. Tenant isolation is a
verdict, not a measurement.

## Limits

One deterministic run of the synthetic lane at the named commit. The probe
proves tenant isolation at the lane's serve seam and the observability of
its denial, exactly as the R-1 suite proved it at both seams; it makes no
authorization claim (authorization is the Platform adapter's concern, not
this seam's).
