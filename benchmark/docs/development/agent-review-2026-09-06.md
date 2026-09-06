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
coverage. Exact per-database selected residency is now persisted and supplied
by configuration or authenticated admission records. Targeted verification
passed as recorded below; global remote control declarations remain unfinished.

Active writer reconciliation now checks durable deletion before skipping
already-active databases. It removes replication lanes with acknowledgments
before retiring the route, retaining maintenance storage. The connected-peer
runtime regression and follower deletion implementation remain outstanding.

The initial encrypted admission restart run, session `50243`, compiled but
failed all three tests at production root validation. Subsequent fixture and
production fixes led to the passing run recorded below. No full
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

Session `13278` passed all 17 server configuration tests but exposed a
`FormatMismatch` during encrypted restart. Ownership validation rejected files
created by the encrypted VFS itself. It now accepts the exact main, WAL, and
rollback-journal companion formats, with 64-character lowercase hexadecimal
generation identifiers. These must remain regular files beside both a valid
owner record and the logical database. Six ownership tests passed, including
malformed-name, orphan, directory, and symlink rejection. Independent review
found no material defect in this fix.

Final server verification passed in session `47728`:
`nice -n 19 cargo test -p terrabase-server --lib -j 2 -- production_admission::tests config::tests --nocapture`,
20 passed, zero failed. Real encrypted `AdmissionHost` replay covers both
preparation-only and accepted deletion, retained fences at the same epoch,
matching retries, and an unrelated document write after restart. The older
helper test now supplies usage placement and chooses its first accepted cutoff
after restart; accepted-deletion retries retain their original cutoff.

The fixture's readiness callback does not establish production replication
readiness. A delayed, already assigned control-plane cutoff arriving after a
restart requires its own accounting test. No production accounting semantics
were changed by the fixture correction.

The Go VM configuration producer now carries an explicit selected Region set,
renders each database only in selected Regions, and uses the host's local Region
for its physical owner. Focused `vmengineconfig` and `internal/testengineconfig`
tests passed. Independent review identified further work: pending hosts need an
explicit observer-absence path, and remote database control declarations must
remain available independently of local storage selection. A global proposal
must not fail merely because the receiving host does not store every database.

Placement verification subsequently passed in session `88323`:
`nice -n 19 cargo test -p terrabase-tbep placement:: --lib`, 18 passed, zero
failed. Coverage includes mixed residency in one accepted successor across
restart, historical acceptance after registration, exact selected sets,
conflicting owners, persisted residency corruption, and merged catalog bounds.
The transition fixtures preserve immutable residency and supply the required
old-pair fence receipts.

The TBEP regression
`explicit_empty_cut_stays_empty_and_baseline_mismatch_requires_snapshot` also
passed in session `9519`; that combined run initially had two placement fixture
failures subsequently corrected in `88323`.
