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

The next auth integration series adds projection, exact scalar sorting and
explicit one-level joins to the producer `Find` action and served OpenAPI.
Queries retain at most 4 MiB, return at most 1 MiB, and share a 64 MiB/one-million
row scan budget across base and related models. Unsorted pages stop when filled.
Astra review caught and corrected unnecessary full scans, missing incremental
bounds, and unsafe transaction error classification before qualification.

Rust evidence: all 26 auth tests passed in `78007`; the four focused join tests
passed in `8743` after adding retained-page bounds and one-to-one/duplicate-output
coverage. Eight auth/OpenAPI server tests passed in `10052`. The real encrypted
QUIC HTTP fixture now checks staged cross-model joins before projection,
ordinary-read isolation, invalid query no-store headers and compound external
identity uniqueness. Its final run passed in `68229`, including refusal to attach
one `(issuer, accountId)` to another user. Exporter `82926` and bundle integrity
regression `71506` passed; SDK metadata was regenerated from that local bundle.

The SDK now exposes an official `@better-auth/core` 1.7.2 factory and a bounded
transaction callback helper. Full adapter operations, including async official
transforms, are serialized and poison the transaction on failure even if caught.
The callback executes once; uncertain commit errors retain reconciliation
identities. Independent cleanup cancellation allows rollback after caller abort.
Schema readiness includes mapped field and compound unique constraints through
named producer index operations; any failed provisioning blocks data operations
and transaction begin. Astra re-review found no remaining material defect in
these revised boundaries. Plugin-wide qualification, nonunique index/migration
support, live dashboard composition, deterministic forwarding saturation and
real cloud/Region release gates remain unfinished. The local SQLite bridge is
still consumed and has not met its retirement gate.

Final SDK verification for this series: `CONTRACT_SOURCE=../terrabase/contracts
make check` passed in session `91534` (287 tests passed; 12 environment-gated
tests skipped). The pinned official-factory regressions cover generated IDs,
date transforms, custom names, normalized transactional joins, empty selection,
failed schema provisioning, compound indexes, async transform invocation order,
caught transform failure rollback and unawaited operation draining. These
request-capture tests verify orchestration, not a live product boundary.

The final diagnostic npm artifact qualification passed on Node 24.20.0 in
session `3586`. Tarball SHA-256:
`02742fd46997f90d1c375283c851b7648bca151abfa61ed8d5a44004e2204ff8`.
Artifact and receipt are under
`/tmp/terrascale-auth-factory-qualification-20260907-final/`.
The clean auth consumer explicitly installs the pinned optional
`@better-auth/core@1.7.2` peer (MIT in its installed package manifest).
This was diagnostic mode with a dirty worktree; Node 22, immutable publication
and release admission remain unqualified. The host's mise npm wrapper required
a temporary PATH entry pointing to the actual npm-cli.js for CLI provenance
inspection; no global toolchain files were modified.

The next dashboard integration preserves the upstream ULID migration and moves
production identity persistence from the obsolete operation binding to the
public `AuthPersistenceClient` and official Better Auth factory. Dashboard
provides its complete core/plugin/custom schema, shares the resulting authority
with billing and notices, and initializes that authority before scheduled mail
delivery. The same lazy secondary-storage instance is included in the options
used to construct the adapter. Existing public database credentials and PROXY
transport apply; missing production configuration still refuses startup.

Review corrected three composition defects: an omitted custom-schema plugin,
missing cold scheduled-worker initialization, and unawaited multi-step guard or
recovery operations escaping transaction completion. Wrapper callbacks now
serialize and drain complete operations and poison failures before final owner
checks or commit. Deferred regressions verify owner deletion and failed recovery
notice rollback. Re-review found no remaining material issue in these fixes.
These tests use consumer fixtures and do not qualify a live production authority.

The SDK's public unique `.index()` extension maps schema names, participates in
transaction serialization, and rejects malformed success results. A concrete
`TerraScaleAuthAdapterError` preserves producer error details. Producer predicates
now use the pinned Kysely/Drizzle SQL grouping and null semantics, including
protection against OR terms bypassing AND tenant guards. The memory adapter's
predicate behavior differs and is not the reference for this contract. Review
caught and fixed a null-inequality regression before final validation. The
producer ADR and served OpenAPI describe the contract; the exporter and SDK
metadata were regenerated.

Validation for this series: all 29 auth state-machine tests passed. Server
`auth_runtime` tests passed in session `82545` (five, including the actual
encrypted QUIC staged/committed HTTP fixture). Exporter `32456` and the contract
bundle integrity regression `58698` passed. The SDK's
`CONTRACT_SOURCE=../terrabase/contracts make check` passed in `84150` with
289 tests passed and 12 environment-gated tests skipped.

Diagnostic npm artifact qualification passed on Node 24.20.0 in `42808`.
Artifact: `/tmp/terrascale-auth-dashboard-qualification-20260907/terrascale-sdk-0.3.0.tgz`.
SHA-256: `6133a1df3db7941f6cda8ff28a3d2238911acfb6065b42e4c1a598de954ac924`.
The receipt remains beside the tarball. This is diagnostic qualification;
Node 22 and immutable release publication remain outstanding.

Dashboard verification finished with `make check` in session `72162`: contract
checks, install, formatting/lint, all 203 test files (1,257 passed; one skipped),
Vinext production build, bundle budget, and Wrangler deploy dry-run passed.
Explicit TypeScript checking also passed after correcting the deferred fixture's
callback types. The MFA browser fixture now sets its local auth origin before
module import, so a production URL in the invoking shell cannot change its
cookie expectations. The secondary-storage concurrency regression verifies
retry after the official SDK's `unique_conflict` error class.

The local SDK installs as a package through npm `install-links=true`, ensuring
Dashboard's Better Auth plugins and the SDK share the app's core peer instance.
This replaces the prior symlink's separate, incompatible plugin type registries.
Source-backed corrections to preexisting dashboard checks preserved other
agents' changes: current Worker entrypoint, device authorization metadata,
public-principal fixtures and flat request-limit errors, adopted OAuth provider,
and MCP tool ordering. Billing consent constants moved intact to a shared module
so client code does not import a backend module. No prices or ULID APIs changed.
Browserslist/fflate patch updates and required browser metadata updates reduced
the reported dependency audit from two vulnerabilities to zero.

Wrangler dry-run emitted a 1,592.18 KiB gzipped upload with the existing PROXY and
ASSETS bindings; it did not deploy or exercise production credentials. The
standalone package budget reported 13.6 KiB CSS and a 260.2 KiB Worker entry.
Vitest still emits missing dependency source-map and post-test close-timeout
warnings, with successful process exit on the passing run. Live provider,
region concurrency, complete plugin/SDK parity and launch gates remain open.

## Maintained Better Auth authority suite: partial execution, not qualified

The SDK now pins the published MIT-licensed `@better-auth/test-utils@1.7.2`
suite and `better-auth@1.7.2` as development dependencies. Upstream source is
tag `v1.7.2`, commit `ba12fcdfa774ca27d417079dbac0b1b5894ccaf2`; package-lock
records registry integrity. Its normal, transaction, auth-flow, joins,
case-insensitive, and client UUID suites run through the built SDK's public
network client. Database-generated numeric IDs remain unsupported.

The ignored `admitted_http_router_serves_external_node_consumer` Rust test
serves the production auth router on a disposable loopback listener backed by
the existing encrypted two-voter QUIC fixture. It passes configuration to an
external Node consumer, owns its process group, bounds execution and shutdown,
and cleans up on failure. The SDK runner rejects failed, skipped, empty, and
unhandled-error runs. Reproduction and limitations are in SDK ADR 0003. This
does not exercise full daemon startup, real key delivery, OCI, or WAN behavior;
the second voter uses a test receiver loop.

The first two executions (`89835`, `28218`) exceeded the initial 180-second
fixture limit. Verbose output showed real forward progress and a join test
timeout. After sharing automatic schema provisioning across adapters from one
factory, diagnostic run `43640` recorded 37 passing cases, including the earlier
join case, before the root deliberately terminated its child process group to
correct the fixture's frame limits. Rust cleanup completed and the run exited
101. None of these runs is a qualification pass. Logs are
`/tmp/terrascale-official-auth-authority.log`,
`/tmp/terrascale-official-auth-authority-diagnostic.log`, and
`/tmp/terrascale-official-auth-authority-cached.log`.

Review found that the fixture allowed 4 KiB frames although auth permits 2 MiB
RPCs and production configures 16 MiB frames. The fixture now uses the production
frame ceiling with a compatible bounded byte budget. `AuthNetwork::new` rejects
local endpoints below the supported auth ceiling. Its new regression rejects
1 MiB and accepts 2 MiB; review caught and fixed the regression's missing Tokio
runtime. Focused server tests passed in `61252`: six passed, one ignored external
consumer test. The corrected frame limits have not yet been exercised by a full
maintained-suite run.

Automatic schema provisioning caches only validated successful or in-flight
physical model/ordered-field definitions within one client/database factory.
Failures clear the cache for retry. Explicit named indexes and transactional
indexes remain actual operations. Independent review found no remaining
material cache issue. SDK `CONTRACT_SOURCE=../terrabase/contracts make check`
passed in `68569`: 290 passed, 13 environment-gated tests skipped. The live
authority test accounts for the additional skip. No ULID changes were reverted.

Two follow-ups remain explicit. Upstream cleanup retains arrays with multiple
created rows, so later tests repeatedly issue durable deletes for historical
rows; later diagnostic cases took 15–25 seconds. The suite was not patched and
missing-row writes still preserve quorum and idempotency semantics. Separately,
review identified a possible two-connection-per-peer contention cycle between
forwarded requests and consensus. It needs a controlled reproduction and a
transport capacity fix before claiming saturation safety. Negotiated peer frame
limits also remain a runtime concern beyond local startup validation.

## Reserved transport and deeper adapter execution

The subsequent unmodified maintained-suite attempt (`66710`) used production
frame limits and stopped after 38 passes: `findMany` with joins and a where
clause exceeded both 30-second test and cleanup limits. It exited 101 with
one failure and 311 unexecuted cases. The isolated case passed in `88679`,
while that filtered run correctly failed qualification with 349 skipped cases.
Logs: `/tmp/terrascale-official-auth-authority-production-limits.log` and
`/tmp/terrascale-auth-isolated-join.log`.

The SDK now has an opt-in, test-only Vite transform for the upstream utility's
cleanup defect. It checks the exact module path and upstream SHA-256
`2993733da66175a5e8a13180a15199ceb398e99439cf92619c24e08a012c9f33`.
It removes successfully deleted rows from tracking, retains failed/unknown
rows, and leaves adapter assertions intact. Behavioral tests verify those
semantics. Installed dependencies and SDK output remain untouched. The runner
requires proof that the transform ran and labels these results separately.
The transformed hash is
`a0bf76aa93c5b2df8ebb29546bee1e093e9ccd50a4544c916213e63600a554e4`.

Cleanup-corrected diagnostic `11746` ran the actual public SDK/HTTP/encrypted
two-voter path for 559 seconds: 341 passed, one failed, eight unexecuted. It
failed on `auth_authority_unavailable` during the UUID mixed-join query and
exited 101. It qualifies neither the patched nor unmodified suite. Log:
`/tmp/terrascale-auth-upstream-patched-cleanup.log`.

That failure exposed missing retries for ordinary auth POST reads. Only named
`findPage` and `count` now opt into the existing bounded transient-read retry
policy. They explicitly suppress global mutation-key generation. Transactional
queries retain stable operation identities; mutation, denial, cancellation,
and Retry-After behavior remain under the existing rules. Independent review
found no material issue. SDK `CONTRACT_SOURCE=../terrabase/contracts make check`
passed in `97433`: 295 tests passed, 13 environment-gated tests skipped.

Member protocol 2 now reserves replication, auth-client, and auth-consensus
sessions and frame-operation capacity. Auth classes have one slot per local
direction; replication has one slot total, giving five sessions per peer.
Handshake capacity is separately reserved for incoming and outgoing attempts,
within the unchanged configured total operation ceiling. Topology validation
requires enough capacity for simultaneous peer sends and receives. ADR 0047
records the private protocol change and remaining composition requirements.

Review and execution caught and fixed shared-counter accounting, permit leaks
during drain, status sampling that removed pending reservations, and a
handshake race that could leave each side retaining a different socket.
Reservations now precede dialing or successful handshake replies and are
released on cancellation/failure. Auth and replication dispatch reject
method/class mismatches. Regressions exercise all five sessions, opposite
directions, full-duplex replication, occupied client permits, rejected excess
dials, pending status sampling, and cancellation cleanup. Independent review
found no remaining material defect in the inspected transport implementation.

The six focused auth runtime tests passed with the directional transport in
`98474`; the external consumer test remained ignored in that command. Strict
transport Clippy passed in `39518`. Full shared daemon composition,
multi-group WAN/failure qualification, and final maintained-suite receipts
remain outstanding. ULID and concurrent agents' changes were preserved.


Further verification retained all ULID and concurrent changes. Final transport
unit tests passed 16 cases; authenticated physical WAL/receipt transport passed
one case, and replication class dispatch passed two cases. SDK diagnostic
artifact qualification passed on Node 22.23.2 and 24.20.0, source revision
`d534cceea97ea9df85de37cdac9c9f4946204c9a`. Tarball SHA-256:
`dc29a3cc37b2f32dc450f5b976be7555d9b9613d88e5f0dd2fe4715f830e6866`.
The receipt explicitly has `release.eligible: false`; this is diagnostic
consumer evidence, not a publication qualification.

The next cleanup-patched suite attempt (`71047`) passed 70 cases, failed one
on a subsequent read after updateMany, and left 279 unexecuted. The SDK retry
eligibility change did not enable retries: configured attempts still default
to zero, including in this integration. The fixture also still used only eight
total transport operations. Production limits are now extracted unchanged
into a shared helper used by production startup and the external Node fixture.
Small-limit focused auth tests remain unchanged; six passed in `93011`.
Independent review found no material issue with the extraction.

With the actual production transport policy, diagnostic `56375` still failed:
19 passed, one failed, 330 unexecuted, exit 101. A one-to-one join findOne
returned `auth_authority_unavailable` (503). Log:
`/tmp/terrascale-auth-production-limits-diagnostic.log`. Thus the small fixture
policy does not fully explain the intermittent authority failures. Both the
unmodified and cleanup-patched maintained-suite gates remain unpassed;
underlying authority/transport diagnosis continues.


Opt-in authority diagnostics in `56066` identified the failure: an outbound
AuthConsensus connect returned `Saturated`; the linearizable read barrier then
returned `QuorumNotEnough` with only voter 1. This attempt passed six cases,
failed one, and left 343 unexecuted (exit 101). Log:
`/tmp/terrascale-auth-authority-errors-diagnostic.log`. Independent source review
confirmed OpenRaft read barriers can overlap ordinary replication RPCs. Each
opened a fresh session against the single outbound class slot. The transport
reservation is working as designed; auth RPC admission must coordinate callers
across all groups sharing the endpoint. A bounded admission fix is in progress.
