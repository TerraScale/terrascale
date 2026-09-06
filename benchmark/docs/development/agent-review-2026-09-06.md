# Concurrent change review

The existing ULID migration remains intact. Review fixes preserve the typed
`from_text`, `to_text`, and `write_text` APIs and the upstream generator.

Server integration fixes correct the optional webhook OpenAPI body, lifecycle
type paths, public encryption imports, and crate-visible daemon error type.
`TenantScope::tenant` now exposes its validated tenant through a read-only
accessor. The RF=2 test reads SQLite's signed integer sequence and explicitly
checks conversion to an unsigned sequence.

`nice -n 19 cargo check -p terrabase-server --lib -j 2` passed in session
`31017`. This verifies the integration fixes, not subsequent placement and
configuration edits. Native recovery signer and branch import warnings remain.

Independent review found that placement catalog expansion invalidated older
acceptance and that cluster membership Regions were incorrectly imposed on
every database. Historical proposal validation was separated from new-stage
coverage. Exact per-database selected residency persistence and configuration
integration remain under review and require targeted test execution.

Active writer reconciliation now checks durable deletion before skipping
already-active databases. It removes replication lanes with acknowledgments
before retiring the route, retaining maintenance storage. The connected-peer
runtime regression and follower deletion implementation remain outstanding.

The initial encrypted admission restart run, session `50243`, compiled but
failed all three tests at production root validation. The fixture is being
corrected; this run is not evidence that deletion replay passes. No full
repository check or production readiness claim follows from the library check.

The engine regression
`baseline_digest_distinguishes_branch_provenance_and_is_stable` passed with
`nice -n 19 cargo test -p terrabase --lib -j 2` and that test filter, session
`37416`: one passed, zero failed. This verifies the retained branch provenance
digest distinction, not the full TBEP repair exchange.

Further execution found a production lazy-open defect: a new writer's directory
was checked for existence before `EngineConfig::durable` could create it. The
Member now validates the confined path, creates its directory, and revalidates
before opening SQLite. Independent review found no material defect in this
ordering change.

Session `13278` passed all 17 server configuration tests, including required
selected residency. All three deletion tests now reach encrypted restart but
fail there, with the direct Member test reporting `FormatMismatch`. That storage
failure is under investigation. First admission passing does not establish
successful restart.

The Go VM configuration producer now carries an explicit selected Region set,
renders each database only in selected Regions, and uses the host's local Region
for its physical owner. Focused `vmengineconfig` and `internal/testengineconfig`
tests passed. Independent review of this producer integration remains pending.
