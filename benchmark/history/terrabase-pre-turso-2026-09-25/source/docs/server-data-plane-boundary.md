# First server document data-plane boundary

Status: **design brief / no public document endpoint approved or implemented**.
This proposes the first server caller of the existing typed document engine and
the gates for a later client transport. It does not turn an administrative or
member-replication listener into a client API. The accepted first document
semantics are in [document-contract.md](document-contract.md); distributed
source, apply and visible-read gates remain in
[distributed-document-write-apply.md](distributed-document-write-apply.md) and
[distributed-document-apply.md](distributed-document-apply.md).

## Source and requirement identity

TerraBase HEAD is `352d9f61185d1b685e3032519579040d169ad47d`.
These SHA-256 identities describe the inspected uncommitted tree on
2026-09-24; refresh any moving source before implementation.

| Path | SHA-256 | Evidence |
| --- | --- | --- |
| `GOAL.md` | `9cd9be4a029199e6c1f9e1a6f1eab59a51e3a287127c0d724d445b8264fc404a` | §§6, 8, 9, 11: typed data contracts, authorization, bounded query/metrics, member transport |
| `docs/document-contract.md` | `8b8f0cfea7b2ce4591a33343c212a8d9dafe13864686ad6deccdd1015f944850` | first scalar, point, conflict and one-shard transaction semantics |
| `docs/evidence-ledger.md` | `b4b56e821a12b77aa63a36bd804ed7de628645c12a1de6e8608d5a50ba72fa58` | DEC-002/003/006 and PROD/ENG/RPC/IDX gates remain open |
| `docs/architecture.md` | `b4cbccec8dd1e8b8da67843117c9a3865bc8b22a4409abed7f814f8a5ce7edb1` | public authorization/visibility distinct from member gRPC |
| `docs/distributed-document-write-apply.md` | `ffea29394dc36763ed5570f7676108809d63de37134596c7d6c4cd7f43730164` | Packet A source and later server gate |
| `docs/distributed-document-apply.md` | `4df3ef0c42d0df4713b01277b2bd1a516bb14171e69d722a4e28d648859b2e50` | receiver D5, visible read and applied-readiness gates |
| `crates/tb-server/src/http.rs` | `14d4bcf9429bda3212228111e82191ccb9ca2d6c5ef32c464212656121d69334` | fixed loopback `/healthz`, `/readyz`, `/metrics` only |
| `crates/tb-server/src/state.rs` | `0735ff2833aa7049fcc29b56adb1e640651c751c75d2c167f9e1074c7f453227` | `LocalServerState` owns raw `Storage`, no document caller |
| `crates/tb-server/src/main.rs` | `deceef23ccc86c47a9f070621a9e2d1ca7da0eb2c055f35cfbfc6893c6761c16` | admin and opt-in member listeners, startup/drain order |
| `crates/tb-server/Cargo.toml` | `7a5fc58425699e9c42eb6b89a0036ee02f0feaf6468487ccdb13857f9316a3fe` | `tb-document` is dev-only |
| `crates/tb-server/tests/server_process.rs` | `b88e18255f3d9b642c3672062784a55274d05068af2a12c7d1d925f620719ad7` | packaged admin routes and document-route 404 |
| `crates/tb-document/src/lib.rs` | `25ac7dce99dbc76156b2a930f1b8d73d12cdd6bf69e25e6ecf33f8912bffb140` | local `DocumentStore::open`, `snapshot`, `read`, schema admission |
| `crates/tb-document/src/producer.rs` | `dc93b4982922afded4ff5e257bb07edb3c9308dabbba77e937cad1e9179dab02` | local `commit_documents` and exact local-durable receipt |
| `crates/tb-document/src/distributed.rs` | `4d22534be5f3fb9d7f4badd6f84f8e9a119e8104dae6f587e94c884f88d1146e` | signed `documents/` open; no read/API caller |

The packaged [server process test](../crates/tb-server/tests/server_process.rs)
asserts `/v1/documents` is 404. `tb-document` is only a dev-dependency of
`tb-server`, and `LocalServerState` retains the raw shard's exclusive lock.
Opening `DocumentStore` again on the same directory is not a composition
strategy. `DistributedDocumentStore` uses its own authority-bound
`documents/` path and currently has no qualified visible-read/API caller.

## Recommended first caller and mode boundary

Add a **transport-neutral, in-process server document service** before any
client route. Its engine owner is an explicit startup mode: raw local shard,
local-v1 document shard, or certified distributed document shard. Exactly one
engine owns each shard directory and process budget. The raw mode keeps the
current administrative behavior. Local-v1 mode opens `DocumentStore` once
with explicit database/shard/origin and local-durable configuration; its
syntactic epoch/configuration is not signed remote authority. Certified mode
opens the accepted authority and separate `documents/` and `inbox/<origin>`
shards; it never infers certification from an existing local-v1 directory.
Validate mode, identity, paths, budgets and any prospective listener before
creating or changing a shard directory. No implicit local-to-distributed
upgrade or shared raw/document handle is in this packet.

The service accepts a **server-minted authorized scope**, never a tenant,
database or collection claim copied unchecked from a request. A future auth
adapter owns minting that scope. Until one is selected and verified, the
service has no externally reachable document route. Its first two operations
are one point read and one small atomic group commit:

| Operation | Internal request and result | First-slice boundary |
| --- | --- | --- |
| Point read | Authorized tenant/database/shard/collection, `DocumentId`, one visibility cut; typed `Absent`, `Deleted`, `Value` or `Conflict`, complete canonical siblings and causal context | Call `DocumentStore::snapshot`/`read` in local-v1 mode; charge encoded output and keep the read permit until serialization completes. No silent winner or unbounded result. |
| Atomic commit | Authorized shard/collections, caller-stable `TransactionId`, sorted unique `(CollectionId,DocumentId)` writes, immutable schema bindings, explicit observed causal contexts, typed live values or tombstones | Call `DocumentStore::commit_documents` once; return its original event/receipt on an exact retry and identify the result only as **local durable**. Reject stale bindings, unsupported numeric/operators, expired or conflicting retry identity, cross-shard scope and over-budget work before claiming success. |

Keep initial schema provisioning as an explicitly authorized operator action,
not an accidental public mutation route. Do not advertise equality/range,
indexes, subscriptions, relationships, cross-shard transactions or stronger
acknowledgments from this point/commit caller. The accepted local value subset
is exact `I64`, distinct Missing/Null, bytewise UTF-8/Bytes and typed conflicts;
the unresolved DEC-006 semantics remain unavailable. Preserve the caller's
causal context and immutable collection binding rather than reconstructing
them from JSON or a mutable collection name.

Admit request and queued work by bytes **and** items before enqueue; admit
read output and serialization buffers before materializing them. Bound
request count/size, output, concurrent blocking work and deadlines. Run
synchronous storage calls in bounded blocking work without a
shard or document mutex guard across `await`. Cancellation after a commit
starts cannot claim rollback; the caller retries the same transaction token
and receives the recovered durable result. Close data readiness on corruption,
uncertain recovery or required backlog work. Map authorization, validation,
schema conflict, capacity/backpressure, retry conflict/expiry and uncertain
commit to distinct typed service errors without leaking another tenant's
record or existence.

## Transport and product choices

GOAL §8 and the repository rule require **gRPC plus LZ4 for member-to-member
traffic**. They do not choose the public client protocol or permit reuse of
the member listener's signed-member mTLS identity as client authorization.
The present administrative HTTP listener is loopback-only by source contract;
keep its three routes and Prometheus scrape separate. A later client listener
is explicit opt-in, independently bounded and fail-closed if TLS, identity,
tenant/database/collection authorization, or its chosen deployment boundary
is absent. Loopback binding alone does not authorize data access.

| Candidate client boundary | Fit and constraint |
| --- | --- |
| In-process typed service, no listener | Exercises server-to-engine ownership and admission now; does not qualify a public API or authentication. Recommended preparatory slice. |
| Separate versioned HTTP client listener | Can use the existing Rust HTTP stack, but needs chosen JSON/binary value and ULID representations plus independently proven TLS/authorization. Do not reuse the admin router as an auth shortcut. |
| Separate versioned gRPC client listener | Gives typed Protobuf contracts, but still needs client identity/authorization, a versioned value mapping and bounded output/stream behavior. Member gRPC/LZ4 does not select or secure it. |

Record a product/security choice before implementing the external adapter:
versioned client wire protocol and value/ULID encoding; client identity and
tenant/database/collection grants; TLS termination and proxy trust; bind
address/default exposure; schema-management authority; and public error and
retry/deadline mapping. DEC-002 still controls advertised durability profiles
and failure domains; DEC-003 controls strong cross-scope operations; DEC-006
controls the remaining query/value semantics. None is filled in by this brief.
The transport-neutral service can be built and exercised internally before
that choice, but it is not evidence of a public API. When chosen, use a
separate authenticated client adapter and listener; do not add unauthenticated
document routes to `http.rs` or a unary-per-document rule to the internal
replication stream.

## Ownership, dependencies and finite gates

1. **Local service composition — server owner.** After an explicit mode and
   authorized-scope contract are accepted, update `crates/tb-server/Cargo.toml`,
   `state.rs`, `config.rs`, `main.rs` and a new document-service module, with
   server tests. Replace raw ownership in document mode rather than opening
   the directory twice. This local-only slice can proceed independently of
   Packet A/B, but it exposes no network route until the client security/wire
   choice. Test actual server composition: two-collection atomic commit,
   visible typed read and conflict/tombstone behavior, same-token retry after
   process reopen, stale binding and cross-scope refusal, budget refusal,
   cancellation/drain, and raw-mode 404/admin continuity.
2. **Authenticated client adapter — server/protocol owners, after the product
   choice.** Own the new listener, versioned wire schema, identity adapter,
   request/output permits, and process tests. Through the packaged binary and
   selected real client, prove unauthenticated/foreign tenant/database/
   collection attempts leave state untouched; exact local-durable result and
   retry survive restart; oversize, concurrency pressure and slow readers
   remain bounded; readiness and graceful drain are honest. `http.rs` remains
   administrative unless an explicit reviewed decision changes that boundary.
3. **Certified distributed client mode — later server/document integration.**
   Packet A source qualification, Packet B receiver apply and replay, budgeted
   D0→D6→D1 visible read, and the real server readiness/ACK gate must pass
   first. Keep local-v1 storage and signed `documents/` separate. A client
   receipt may name only its proved durability stage; remote `AppliedAck` and
   causal read readiness require recovered D5, inbox reconciliation and a
   visible read. A two-replica authenticated member gRPC/LZ4 test plus the
   selected authenticated client path must show the same logical siblings
   after restart despite different physical layouts.

Prometheus-compatible service metrics start with bounded-label operation
count/latency, rejection class, admitted/encoded bytes, in-flight work,
conflict count and local-durable stage, reusing the administrative `/metrics`
exporter without event/tenant/document labels. Later add bounded query scan
versus index choice, examined/returned rows, pagination, index freshness,
build/catch-up, replication D5/inbox lag, subscription backlog and cost
signals as those real callers exist. Equality/range/pagination must first use
the same typed comparator and one snapshot, return each matching document
once with its complete siblings, and prove source-scan/index equality; index
or model advice never licenses incomplete results. Keep all benchmark drivers
and results in `../ts-benchmark`.
