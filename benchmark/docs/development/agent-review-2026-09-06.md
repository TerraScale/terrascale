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

Global placement declarations now persist independently of local storage
admission. Staging reloads the durable catalog under an immediate transaction,
validates immutable owner and residency sets, and commits new declarations with
the proposal CAS. Only explicit local registration promotes a control-only
scope. Independent review found no material defect in this journal change.

The placement run in session `26871` passed all 24 tests with
`nice -n 19 cargo test -p terrabase-tbep --lib -j 2 placement:: -- --nocapture`.
Coverage includes stale-CAS rollback, retained retries after catalog expansion,
signed control-only acceptance across restart, explicit local promotion, and
separate journal connections preserving durable admission. The multi-Region
restart fixture now supplies SFO seats and assignment matching its declared
GRU/SFO residency. The typed ULID APIs and upstream generator remain intact.

Pending-host review confirmed that admission credentials are required and an
absent observer is omitted from serialized ts-node configuration. The regression
now uses a nonnil pending engine material and matching signed host manifest;
`go test ./src/orchestrator/vmengineconfig ./src/orchestrator/vmimage` passed.
Broader Go verification initially exposed a stale TBEP protobuf hash pin,
requiring consumer regeneration and provenance refresh. These targeted results
do not establish full repository gates or production readiness.

The standard `terrabase-contracts` exporter completed in session `98749`.
The producer, exported, and Go consumer `engine.proto` files match byte for
byte. Exporter regression
`bundle_contains_served_openapi_all_protos_and_integrity_metadata` passed in
session `34527`, checking the served document, protobuf sources, and manifest
digests. Regeneration also incorporates existing public database route and
conformance changes; no exported routes were removed. At that stage the auth route annotations
were absent from `api::openapi_json()` and the exported bundle; the later auth
integration below closes that producer-contract gap.

The first full orchestrator command-package run exposed a bootstrap receipt
mismatch: publisher canonicalization discarded Stage declarations. The publisher
now retains them in the durable request. Empty initial bootstrap rejects
declarations, and its fixture clears declarations with assignments. Focused
bootstrap, database-plan, and transition tests passed after correction.
The final full `tbep`, `placementpublisher`, and `cmd/orchestrator` Go package
tests passed. A durable request roundtrip regression covers declaration
preservation. Independent review found no material defect in these integration
fixes; `git diff --check` passed in both affected repositories.

Auth integration review preserved typed ULIDs and concurrent agent work.
Admitted followers now forward to the elected voter over authenticated QUIC;
forwarded client work has an independent eight-task limit. Recoverable peer
failures leave the listener running. Finished frames wait for peer ACK before
connection disposal, fixing a response-delivery race. Independent review found
no further material defect in these revised transport and dispatch paths.

Verification: all 22 auth library tests passed (session `40486`); all 11
production transport tests passed (session `9965`), including a 512 KiB reply
followed by immediate connection drop; all eight targeted server auth/OpenAPI
tests passed (session `12839`). OpenAPI fixtures now account for 64 operations
and the enlarged Scalar document. The real encrypted two-voter HTTP fixture
covers staged/committed/rolled-back visibility and concurrent operations on both
authorities. Bounded overload rejections are allowed while reads and writes
must make progress. This is concurrent smoke coverage, not deterministic proof
that eight forwarding slots are occupied; controlled saturation, leader
observation and WAN qualification remain unfinished.

The merged public/auth OpenAPI was exported with the standard producer binary
(session `88764`). TypeScript build and standard local-source metadata
generation passed, producing 64-route manifests/descriptors; both generator
`--check` modes passed. This local regeneration does not publish or qualify an
immutable release artifact.

The regenerated bundle integrity regression passed in session `64131`, checking
the served OpenAPI, included protobuf sources, and recorded content digests.
