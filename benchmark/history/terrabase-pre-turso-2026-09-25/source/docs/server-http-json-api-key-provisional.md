# Provisional HTTP/JSON client boundary with scoped API keys

Status: **planning assumption / no listener or public contract approved**.
This follows [server-data-plane-boundary.md](server-data-plane-boundary.md)
using the coordinator's provisional client direction while optional product
preferences remain unanswered. A later user decision may replace this
transport/auth adapter without changing the local document engine contract.
No implementation, endpoint or durability claim follows from this brief.
The concrete first-caller recommendation and default formats are in
[server-http-json-first-caller-decision.md](server-http-json-first-caller-decision.md).

## Pinned evidence and exact scope

TerraBase HEAD is `352d9f61185d1b685e3032519579040d169ad47d`.
The following SHA-256 identities are of the inspected **uncommitted tree** on
2026-09-24. Server source is moving; refresh before any implementation.

| Path | SHA-256 | Relevant seam |
| --- | --- | --- |
| `GOAL.md` | `9cd9be4a029199e6c1f9e1a6f1eab59a51e3a287127c0d724d445b8264fc404a` | §§6, 11 require typed semantics, authorization, bounded work and credential rotation |
| `docs/document-contract.md` | `8b8f0cfea7b2ce4591a33343c212a8d9dafe13864686ad6deccdd1015f944850` | local-v1 typed point and one-shard commit subset |
| `docs/server-data-plane-boundary.md` | `4c1e8a3413616226d1e763d5c3c84d02bd76fd821616383ab55276fa93079e92` | mode separation and transport-neutral server caller |
| `Cargo.toml` | `ce7d8778e5f690757d1cbff8829211b7268cc5630670ed937b2812ef2231d14a` | pinned Axum, Tower, Serde JSON, rustls, sha2 and reqwest entries |
| `Cargo.lock` | `278c8b7653543c1b33df0c5c955cc0f90652e2b9439c50f2e852bf06b4bc21d4` | exact resolved versions, including transitive TLS/body/security helpers |
| `crates/tb-server/Cargo.toml` | `76846348846be12efe8525c6e396c78ce4f910eb20a228a20d4aefede3c805a8` | `tb-document` and `tb-format` are production dependencies; service work is active, not accepted |
| `crates/tb-server/src/http.rs` | `14d4bcf9429bda3212228111e82191ccb9ca2d6c5ef32c464212656121d69334` | loopback admin routes, bounded headers, sockets and handlers |
| `crates/tb-server/src/state.rs` | `0735ff2833aa7049fcc29b56adb1e640651c751c75d2c167f9e1074c7f453227` | current raw local owner and readiness |
| `crates/tb-server/src/main.rs` | `deceef23ccc86c47a9f070621a9e2d1ca7da0eb2c055f35cfbfc6893c6761c16` | listeners bind before shard open; no client listener |
| `crates/tb-server/src/document_service.rs` | `d077145e8177bfa3dadc150f58d56286960fc01c15711ea599030644fa5ab121` | in-process local-v1 service; fixed scope is not an API-key grant; refresh before implementation |
| `crates/tb-document/src/lib.rs` | `fec608bcaebb630273eddb05671044ebf460e8a3f21678422a1644fc55f254f6` | `DocumentStore::snapshot` and typed point read |
| `crates/tb-document/src/producer.rs` | `e9dc914a5979daf79402bdb78749c5b25d5855ca88cd9164f5d801a4bd04d9b3` | one-shard commit and local-durable retry |
| `crates/tb-server/src/replication_server.rs` | `4b6641546346578c354f7d7636fda35d22c82163fe01e522317f42cd8df62e80` | separate member TLS material validation, not client authorization |

The administrative router serves only `/healthz`, `/readyz`, and `/metrics`.
Its HTTP parser caps sockets, headers and handlers, but it has no document
authentication. The member listener's signed-authority mTLS and LZ4 envelope
are specific to TerraBase-to-TerraBase traffic; this proposal uses neither as
an implicit client identity nor as a public JSON encoding rule.
`LocalV1DocumentService` currently has only its fixed server-configured scope;
it has no running-process caller or per-key read/write grants. The adapter
must add those checks before invoking it after service composition is accepted.

## Proposed client adapter and startup gate

Keep the client adapter separate from the admin router and member gRPC
listener. No client configuration means **no client listener**. A configured
client listener uses HTTPS only, including on loopback. Its default bind is
one explicit loopback address; a nonloopback bind requires a separate
operator opt-in and an explicit concrete address. Reject wildcard binds in
this first profile until their exposure policy is reviewed. The opt-in alone
cannot bypass TLS, key registry or process-memory admission.

Configuration supplies paths to a server certificate chain, its private key
and an API-key verifier registry; it never embeds secret bytes in CLI flags,
environment variables, URLs or logs. Read these files under explicit size
caps from regular protected files, reject symlinks/unsafe permissions on the
supported platform, parse the chain/key pair and validate the registry
before binding or accepting clients. Bind the configured sockets before
opening a new shard directory, as current startup does for occupied ports;
do not mark the client listener ready until the selected document mode and
all auth/TLS material are valid. A missing, malformed or unreadable source
keeps the remote API closed while administrative diagnostics remain available.
Treat TLS rotation and registry replacement as validated atomic generation
changes; if a reload cannot be validated, close client readiness and reject
new client work rather than continuing with uncertain authorization. Keep
old in-flight requests bounded and document whether revocation applies at
request admission or also cancels them. This first proposal applies
revocation to **new admissions after the successful generation swap**;
already admitted work may finish under its old grant.

An API key is a generated high-entropy opaque bearer secret with a public
lookup ID; do not accept human-chosen passwords as keys. Store only its
verifier, exact tenant/database/shard and collection allowlist, read/write
permissions, configured rate/burst and revocation state. On a bounded
`Authorization: Bearer` header, look up the ID, compare the verifier in
constant time, and mint a private authorized scope. Missing, malformed,
unknown and revoked keys fail before JSON body materialization. Never accept
an API key in a query string. The verifier scheme is conditional on at least
256 bits of CSPRNG-generated secret entropy: `sha2` SHA-256 plus `subtle` constant-
time equality is a candidate; human passwords would require a separate
password-hashing design and are outside this assumption. Raw keys and header
values never enter tracing, errors, metrics, panic formatting or access logs.
Log only bounded operation/result classes and an operator-safe audit key ID
if an explicit retention/redaction policy later permits it.

The server's local-v1 mode has one configured tenant/database/shard. Compare
that exact scope with the key grant, then check **every** requested collection
against the allowlist and operation permission before `DocumentStore::read`
or `commit_documents`. A URL/body tenant, database, shard, schema generation
or collection claim is never authority. Require immutable schema binding and
explicit observed context for each sorted unique write; the operator schema
route remains separate. Return only typed `Absent`/`Deleted`/`Value`/
`Conflict` and a **local-durable** commit receipt. Preserve `TransactionId`
across retries and map expired/conflicting/uncertain outcomes distinctly.
No `AppliedAck`, remote-durable claim or certified distributed read is
available through this adapter until Packet A, receiver apply/replay, visible
read and real server gates pass.

## JSON, limits, dependencies and finite checks

Use a versioned `/v1` client route family only after its exact wire schema is
reviewed. JSON must encode canonical ULID strings through validated
conversions, exact signed `I64`, distinct Missing and Null, and binary bytes
without lossy text coercion; reject fractions, out-of-range integers,
unknown operators, duplicate logical writes and unsupported array/multikey
predicates. Parse a bounded body into typed request structs rather than a
general `serde_json::Value` mirror. Keep output sibling sets complete; if a
legal result cannot fit its admitted response cap, return capacity rather
than silently dropping conflicts. Do not enable request compression in the
first profile; reject unsupported `Content-Encoding` before body allocation.

Start review with the admin parser's 16-KiB/32-header and five-second header
caps, plus a **provisional** 4-MiB JSON request cap, 4-MiB encoded response
cap, eight client sockets and four in-flight handlers. These numbers are
implementation admission ceilings, not a performance or full-valid-event
promise; adjust them only with a peak-memory and near-limit document proof.
Reserve request, queued, parsed and output bytes/items in the existing
process budget, keep permits through serialization/write, bound slow readers
and blocking tasks, and use deadlines without a shard lock across `await`.
Global pre-auth connection/concurrency caps and a cheap unknown-key attempt
limit precede a **per-key** configured rate/burst and concurrency policy.
The per-key limiter must use a reviewed maintained package or an existing
qualified middleware primitive; do not silently implement a new unbounded
token-bucket map. The linked first-caller packet recommends bounded direct
`governor 0.10.4` limiters and concrete defaults; its compile and process
gates remain pending.

The existing pinned stack is Axum `0.8.9`, Hyper `1.11.1`, Tower `0.5.3`,
Serde `1.0.229`, serde_json `1.0.151`, rustls `0.23.37` and sha2 `0.11.0`.
`tokio-rustls 0.26.5`, `tower-http 0.6.11`, `subtle 2.6.1` and
`zeroize 1.9.0` are currently lockfile **transitives**, not proof of direct
server API coverage; promote only needed crates as explicit dependencies
with reviewed features and tests. Reuse Tower for global concurrency,
`tower-http`'s reviewed body-limit feature if selected, and rustls with a
reviewed Tokio TLS acceptor rather than a custom TLS protocol. `rcgen` is
already a server dev-dependency and workspace `reqwest 0.13.5` is a candidate
real HTTPS test client. No manifest change is authorized by this brief.

The later client-adapter owner should prove through a packaged HTTPS client:
incomplete TLS/key/registry fails before exposure; nonloopback without opt-in
fails; plaintext, missing/invalid/revoked key and cross-tenant/database/shard/
collection attempts never read or mutate; exact local read/commit and same-
token retry survive restart; malformed JSON, numeric overflow, oversized/
slow bodies and responses, unknown-key floods and per-key rate excess are
bounded; registry rotation/revocation takes effect at its documented cut;
logs and Prometheus samples contain no secret or unbounded key/tenant labels;
admin routes and member mTLS still behave independently. The selected client
wire schema and auth limits remain reversible until reviewed and tested.

## Conditional implementation briefs and decision boundary

These are handoff boundaries for the coordinator, not approval to start a
listener. First accept the local-v1 service's real process composition and
settle the external wire/auth choices. The service currently exists only as
an in-process module and `main.rs` still opens raw `LocalServerState`.

1. **Client contract and security decision — design/document owner.** Own a
   reviewed versioned request/response schema and key-registry format in a
   new client-contract document, without changing engine or server source.
   Specify exact ULID, binary, Missing/Null, sibling/context, commit receipt,
   error, retry/deadline and key-token encodings; decide TLS certificate trust
   and rotation procedure, supported filesystem/permission policy, browser or
   proxy support, key issuance/revocation workflow, rate/burst defaults and
   unknown-key treatment. Require fixed-size token grammar and bounded key
   registry/cardinality. Resolve the maintained per-key limiter and its
   eviction/expiry behavior with the actual pinned crate API. These choices
   need product/security acceptance because clients and operators will rely
   on them; this draft merely proposes one coherent profile.
2. **Authenticated adapter — one server source owner, after the contract and
   local service are accepted.** Own proposed new
   `crates/tb-server/src/client_auth.rs` and `client_http.rs`, and the sole
   integration edits to `crates/tb-server/src/{lib,config,main}.rs` and the
   server/workspace manifests. Coordinate the `document_service.rs` interface
   with its current owner; do not overwrite concurrent local-service work.
   Input is a validated TLS connection and bounded bearer header; output is a
   server-minted grant with exact scope and operation permission. Validate it
   before typed decode and every service call, then translate only typed
   local-v1 points and local-durable commits. Keep the admin `http.rs` and
   member `replication_server.rs` boundaries intact. Prove startup ordering,
   atomic registry swaps, memory permits, cancellation/drain, bounded labels
   and fail-closed readiness through the actual binary.
3. **Real-client verification — test-file owner after adapter integration.**
   Own a new `crates/tb-server/tests/client_process.rs` and fixtures under
   that test tree; use a verifying HTTPS client against the packaged process,
   not an in-memory router alone. Exercise all refusal, scope, retry,
   restart, limit, rotation and independent-listener gates listed above.
   Evidence of a passing in-process service test alone does not qualify an
   externally reachable API. Put any load/throughput benchmark driver and
   result only in `../ts-benchmark`.
