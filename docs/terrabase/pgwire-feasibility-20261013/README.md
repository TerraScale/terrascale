# TS-746: PostgreSQL wire feasibility, 2026-10-13

Review the acceptance gates in [Implementation phases](#implementation-phases) before admitting implementation.

**Status: future backlog, documentation only; disabled by default through absence of a listener.** This artifact implements no switch, listener, server, authentication change or infrastructure. Implementation admission and draft PR publication require separate, explicit serial authorization. It does not assign or displace existing release-critical owners.

Reviewed TerraScale producer: **`e30ffdc287530551b161b0130220be53d3c0475e`**. Initial worktree HEAD and local `origin/main` both resolved to this commit. The existing task worktree is `docs/pgwire-feasibility-20261013`; no fetch or rebase was performed during the initial offline study. Later documentation commits are not new producer evidence. Evidence links and version pins are in [EVIDENCE.md](EVIDENCE.md); deterministic documentation fixtures are in [VECTORS.md](VECTORS.md). Executed checks and limitations are in [CHECKS.md](CHECKS.md).

Publication was subsequently authorized explicitly. The documentation branch was rebased onto fetched `origin/main` **`4561bb7aac8b1556ca466dd91d4731f077507744`** for the PR; this does not change the reviewed producer pin or admit implementation. The initial offline scope/check record below remains historical evidence.

## Decision

A narrow, read-only PostgreSQL transport is technically plausible as a **TerraBase adapter to its existing query service**. It is not available by enabling the pinned Turso .NET provider. Client compatibility, authorization integration, TLS termination and resource accounting remain acceptance gates, not demonstrated properties.

The practical first target is a controlled client issuing the existing single-collection SQL subset and consuming two stable result columns: `id text`, `value json`. Do not advertise PostgreSQL database compatibility, ORM compatibility or arbitrary SQL. Choose this result contract before implementation because schemaless JSON cannot supply stable native column types by inspecting a first row.

### Correction to the old Ku plan

**“SQL writes should become lattice writes like the HTTP SQL endpoint” is incorrect for this producer.** HTTP SQL is read-only:

1. `RunSqlEndpoint` declares a read-only operation and requires `databases:read`.
2. `SqlSubset` accepts exactly one `SELECT`; it rejects writes, transaction commands and placeholders.
3. `QueryService.ReadAsync` calls `BeginTransactionAsync(database, write: false, ...)`.

User text is parsed into `SqlQuery` and rebuilt into engine-owned SQL. It is not passed through to native prepare. The spike's “Read-write SQL as lattice writes” is a historical proposed experiment, not a shipped HTTP capability. Its existence does not establish SQL mutation, replicated SQL transactions or durable PostgreSQL `COMMIT`. TS-746 must not claim any of these.

## Overlap and ownership

Overlap was reported before creating this directory. Search coverage: the current tracked tree, local refs/history, accessible sibling product content and the repository-required Rust reference locations. No current TerraBase pg-wire implementation or named TS-746 owner was found in that bounded search. Ku assignment, unpublished work and GitHub's live state were not queried.

| Existing work | Relationship and boundary |
| --- | --- |
| `docs/terrabase/turso-spike.md`, lines 107, 110–126, 264 and later swap notes | Existing feasibility and lattice-write proposal. Referenced here; never edited. Its native/server separation is corroborated at the actual pin; several historical limits differ from current source. |
| Cached `origin/feat/postgres-turso-import-core` and `...-engine`, `docs/terrabase/import.md` | Adjacent export-import contract, not pg-wire. Existing import owners retain the work. Exact inspected refs are recorded in EVIDENCE. |
| Archived TerraBaseV2 parity row PB-1023 | Prior PostgreSQL wire exclusion under PD-2026-08-26-10 D4. Historical context requiring reconciliation, not proof of today's owner or an automatic veto of this requested study. |
| `src/TerraBase/AGENTS.md`'s `../terrabase-rust` reference | Expected sibling contract checkout was absent at the worktree and canonical project location. Unrelated scratch copies were not adopted as authoritative contracts. Obtain the correct pinned reference before implementation admission. |

Replication, durable ACK, restore, auth, import, engine and CI writers stay with existing owners. This document is a consumer of those contracts. Any change they require is a separate dependency owned by them. No new protocol may access their files directly or silently broaden their guarantees.

## What the pinned provider actually exposes

TerraBase references vendored `Turso.Data.Sqlite` → `Turso.Data` → `Turso.Raw`, with upstream tag **v0.8.2**, commit **`5c168be5ef6a32ac966daf5983502a4f694b37ad`**, plus four repository C# patches. The native package is **Turso.Data.Native 0.8.2**, locked by content hash. The engine folder remains named `Engine/Sqlite` while importing Turso types; a folder name does not identify the active provider.

The actual upstream source separates `sdk-kit`'s C ABI from `postgres/server` (`turso_pg_server`) and `postgres/cli` (`tursopg`). The latter depends on **pgwire 0.36.3** via Cargo.lock. The SDK kit has no server crate dependency. The inspected cached Linux x64 native library has 223 defined dynamic exports, including 77 `turso_*` exports; no export matches `pgwire`, `postgres`, `listen`, `server` or `interrupt`. This corroborates the absence of a callable .NET/native pg-wire server, without claiming a full binary audit across every RID.

| Candidate | Assessment |
| --- | --- |
| Existing .NET/native provider alone | **Unavailable:** no exposed server/accept/start API; query/provider bindings are not a server. |
| Stock `tursopg` opening TerraBase's files | **Reject for this design:** bypasses TerraBase parsing, tenant resolution, metering and engine ownership. Upstream uses `NoopHandler` startup and `process_socket(..., None, ...)`; compatibility docs specify trust auth and plaintext. It executes native mutations directly. |
| Separate proxy calling the existing HTTP SQL API | Possible independent design; retains the HTTP boundary but introduces another service, session/auth custody, extra hops and translation of truncation/errors. Not a stock Turso capability and not selected here. |
| Adapter in TerraBase, calling existing query interfaces | Recommended design direction, subject to auth-owner and hosting decisions. No new storage engine or native build. Evaluate mature server libraries before deciding how much protocol code to own; none is pinned in this repository. No package selection or installation in this task. |

Do not repeat the spike's blanket “Turso does not support two processes on one file” as a current guarantee: the exact pinned README advertises **experimental multi-process WAL coordination**, and the manual documents an experimental CLI option while its older limitations still say no multi-process access. There is no evidence here that TerraBase's native open path enables or safely integrates it. Regardless of upstream support, a file-sharing sidecar is excluded by TerraBase ownership and invariants. Release-asset availability was not verified.

Other exact-pin caveats: upstream extended `Execute` ignores row limits; query responses are collected into a vector; input parameters assume UTF-8 text and NUMERIC is parsed as `f64`; errors become `XX000`. These are reasons to assess an adapter rather than inherit stock behavior. They are source observations, not tests run here.

## Read-only design matrix

**Facts** below describe the reviewed producer or PostgreSQL specifications. **R1** is a proposed first phase; **R2** is a separately admitted extension. Neither is implemented.

### Connection framing and startup

| Concern | Evidence and proposed contract | Gate |
| --- | --- | --- |
| Framing | PostgreSQL typed messages are one type byte, then big-endian Int32 length including itself but excluding the type. Startup-family packets have no type byte. Parse by lengths across fragmented/coalesced input; validate minimums, overflow, trailing data and terminators before allocation. | Offline malformed-length vectors now; future real transport tests for every byte split and peer disconnect. |
| Version | Target **3.0**, `196608`, not full PostgreSQL 18 protocol 3.2 (`196610`). For higher minor versions within major 3, R1 should send `NegotiateProtocolVersion` with minor 0 and rejected `_pq_.*` options, then use 3.0 semantics. Unknown major gets fatal `0A000`. | Test negotiated fallback with explicitly pinned clients; no 3.2 cancel-key layout claim. |
| SSL/GSS | SSLRequest is 8 bytes, code `80877103`; reply is **one byte** `S`, then TLS, then encrypted StartupMessage. GSSENCRequest code `80877104` gets `N` and may proceed to SSL. Plaintext startup is refused before any authentication payload. No plaintext fallback or trust auth. | Hosting/security owners select and verify the TLS termination boundary. An SSL response is not a typed PostgreSQL frame. |
| Startup keys | Require valid `user` and an explicit `database` selector. Proposed selector is `<project ULID>/<database name>`, resolved through existing authorized catalog logic. `user` is an untrusted label, never authority. Allow UTF8 and bounded application name; reject unsupported `options`/GUCs, duplicates and non-UTF8 encodings. | Exact selector and allow-list approved as a new protocol contract; never interpret names as paths. |
| Direct TLS/status | PostgreSQL's direct TLS form requires ALPN `postgresql`; defer it from R1. After owner-approved authentication, send only truthful ParameterStatus values (`client_encoding=UTF8`, `server_encoding=UTF8`, supported string behavior) and `ReadyForQuery(I)`. Do not spoof a PostgreSQL server version to unlock ORM paths. | Classic SSLRequest must work independently of direct TLS. Required startup/status expectations measured per client; reject unsupported requirements. |

The existing Fly configuration exposes an HTTP service, not a pg-wire service. Its HTTP TLS and `fly-replay` behavior cannot simply be assumed to work for PostgreSQL negotiation or TCP sessions. Future hosting work must verify a documented PostgreSQL-aware TLS route or in-process TLS, without adding it here. Do not infer approved infrastructure from this design. TCP sessions need machine affinity; failover closes the session and may retry a read only after fresh authorization. Every serving region remains governed by automatic discovery, not enable switches.

### Simple and extended query cycles

| Concern | R1 proposal | R2/deferred behavior |
| --- | --- | --- |
| Simple `Q` | One bounded statement through `SqlSubset.Parse` → authorized `IQueryService.RunAsync`. Return RowDescription, DataRow(s), `CommandComplete("SELECT n")`, `ReadyForQuery(I)`. Empty query gets EmptyQueryResponse plus Ready. | PostgreSQL permits multiple statements in a simple message; explicitly reject batches rather than execute a prefix. Parse the whole message before work. |
| SQL dialect | Existing SQLite-dialect document subset: collection, supported WHERE/ORDER BY, default 100/max 1000 rows. No pass-through SQL. Reject DML/DDL, WITH, joins, subqueries, aggregates, expressions, aliases, pg_catalog, COPY, SET/SHOW, functions and transaction controls with `0A000` when syntactically recognized. | Versioned PostgreSQL AST translation requires separate design and producer changes; native TursoPG's dialect is not the HTTP subset. |
| Extended messages | Recognize well-framed Parse/Bind/Describe/Execute/Close; return `0A000` for an unsupported extended operation and discard normal messages until Sync. Sync ends recovery with Ready(I); Flush only flushes pending output, never ends recovery. Terminate always releases resources. | Support Parse → Bind → Describe → Execute → Close/Sync only after statement/portal storage and type rules are bounded and tested. Parse contains at most one statement. |
| Parameters and formats | No placeholders, prepared statements or binary format in R1. Never concatenate Bind bytes into SQL strings. | R2 binds `$n` by AST position to validated typed operands. Describe must expose stable OIDs before rows, validate parameter counts/format arrays (0, 1 or parameter count), and reject OID 0 if its type cannot be determined. Binary format 1 needs per-type codecs; otherwise `0A000`. |
| Portals/flow | One command at a time; no portal suspension, pipeline execution or asynchronous SQL notifications in R1. | Execute max rows 0 means no protocol row cap, not exemption from service limits. Positive max rows needs cursor state and PortalSuspended; resume without duplicates. Never rerun a query to simulate a cursor. |

R1 is deliberately a small client contract. No `psql`, Npgsql, pgx, JDBC, Psycopg, GUI or ORM was exercised. Even psql startup/introspection commands can fall outside this subset. Compatibility is claimed only for client versions and settings that later pass integration gates; driver settings cannot act as server authorization bypasses.

### Transactions, consistency and isolation

| Concern | Current fact / proposal |
| --- | --- |
| Autocommit | HTTP SQL opens a local `write: false` read transaction for each operation and disposes it. R1 uses that operation boundary and reports Ready(I). A transport session is not a PostgreSQL transaction. |
| Explicit transactions | `BEGIN`, `START TRANSACTION`, `COMMIT`, `ROLLBACK`, savepoints, `SET TRANSACTION` and two-phase commit are unsupported in R1 (`0A000`). Do not answer successful BEGIN/COMMIT as no-ops. PostgreSQL extended implicit cycles differ from independent service calls; R2 must define this honestly. |
| Read-only transaction extension | Separate optional future admission: pin one authorized engine read transaction/snapshot to the session, bound its lifetime and bytes, release on disconnect, and implement Ready(T/E/I), error-abort and ROLLBACK correctly. `IQueryService.RunAsync` presently creates its own transaction; it cannot silently supply this feature. |
| Distributed semantics | A local read snapshot is not global serializability or cross-region read-your-writes. Preserve the producer's replication/lag and session identity rules. PostgreSQL isolation-level names require demonstrated semantics; no SERIALIZABLE, REPEATABLE READ or SQL lock claim here. |
| Mutation boundary | Reject writes before engine invocation. Never open a writer for client SQL, change capture, mutate lattice stamps, alter synchronous/journal settings, advance ACK/watermarks, or use backup/restore/import paths. Existing usage/query telemetry may persist through its current owned path; “read-only” describes customer data, not zero internal accounting writes. |

Any later SQL-write proposal is a new backlog dependency, not R2 scope. Its owners must first establish AST allow-listing, atomic lattice/CDC conversion, conflict policy, one-writer ownership, visibility, error rollback and exact durable ACK behavior. Native SQL transaction support alone proves none of these. Do not reuse the superseded write-wait part of decision 0010 as a current durability guarantee.

### Rows, types and values

R1 has a fixed RowDescription: `id` OID **25** (text), `value` OID **114** (json), both type length `-1`, typmod `-1`, format 0, table OID/attribute number 0. This applies before the first row, including empty results. `SELECT *` yields the document object in value; projections yield the existing HTTP projected object, including `_id`/`_rev` semantics. The separate id column remains the document id.

| Value | R1 output / future parameter mapping |
| --- | --- |
| Missing, NULL, empty | Missing projected fields and JSON null remain JSON `null` **inside value**; the enclosing value is a JSON object, not SQL NULL. Generic protocol SQL NULL is length **-1**, empty string length **0**, and JSON null bytes `6e756c6c`; do not conflate them. Null predicate operands are rejected today except IS NULL/IS NOT NULL. |
| Integers/reals/numeric | Preserve JSON numeric tokens on output without conversion through double. Existing predicate literals are signed 64-bit integer or finite double, not PostgreSQL arbitrary precision numeric. R2 may accept int2/int4/int8 (OIDs 21/23/20 with range checks) and finite float8 (701); NUMERIC (1700), NaN/Infinity and precision beyond that predicate domain must be rejected until exact semantics exist. Never silently cast NUMERIC to f64. |
| Unicode/text | Strict UTF-8, byte counts, no normalization; preserve composed/decomposed text, CJK and emoji distinctly. Protocol String fields end in NUL; DataRow fields are length-delimited. PostgreSQL text rejects U+0000: reject a raw text field containing it with `22021`; JSON escaped `\u0000` can remain json text and must not be silently recast as text/jsonb. |
| Boolean/JSON | JSON booleans remain JSON true/false; a future bool OID 16 codec uses PostgreSQL text `t`/`f` and a distinct binary codec. R1 uses json, not jsonb (3802); no jsonb equality, normalization or binary-version promise. Future input json/arrays/objects cannot become scalar predicates without an approved query model. |
| Blob/time/other | Provider storage can contain native BLOB values, but current document-query output is JSON. No BYTEA (17), UUID, date/time/timezone, interval, array, enum, domain, range, composite or large-object mapping is promised. Reject unsupported Bind OIDs/formats rather than guess from text. |

The OIDs and wire layouts are PostgreSQL 18 source facts; R2 mappings are proposals. PostgreSQL binary NUMERIC has its own representation, not .NET decimal bytes or IEEE double. Codec-only int64/bool/float/json examples in the vectors are specification documentation, not exposed R1 capabilities.

### Errors, cancellation, bounds and authorization

| Concern | Proposed rule and dependency |
| --- | --- |
| SQLSTATE | Map typed failures deliberately: `08P01` framing, `42601` syntax, `0A000` unsupported feature, `22021` encoding, `22003` numeric range, `22P02` invalid input representation, `28P01` invalid authentication, `42501` insufficient scope, `3D000` unknown/unauthorized database (same message to prevent enumeration), `57014` request cancellation or a separately identified deadline, `54000` generic query-budget/result hard limit, `53300` connection admission, `53200` memory admission, `55000` unavailable local state, `XX000` sanitized unexpected failure. Current SqlSubset/QueryService collapse several failures into Validation; precise mapping needs an owner-approved typed boundary, not native error-message parsing. The current generic “query too expensive” result does not distinguish time from work exhaustion; keep it `54000` unless that boundary supplies a typed cause. |
| Recovery/redaction | ErrorResponse carries S/V severity, C five-character code and M safe message, final NUL. Simple errors finish with Ready(I); extended errors wait for Sync. Fatal framing/startup errors close. Never send engine paths, exception SQL, query values, JWTs, API keys or connection strings. No speculative constraint/serialization SQLSTATE mappings for unsupported writes. |
| CancelRequest | Separate startup-family connection with code `80877102`, PID/cancel capability; no direct response. **R1 does not advertise cancellation and omits BackendKeyData**; an incoming request is closed without executing anything. PostgreSQL docs acknowledge third-party backends may omit the key. Disconnect/deadline still propagates the existing token. R2 needs machine-affine routing and unguessable session capabilities tied to live sessions; never repurpose tenant IDs, auth secrets or process signals. Protocol 3.0 keys are 4 bytes; 3.2 permits variable keys. |
| Backpressure/limits | Reserve memory before frame reads, parsing, result materialization and queuing. Fixed proposed R1 ceilings: startup 8 KiB; normal frontend frame 64 KiB including its length field; SQL bytes must fit within the frame and also pass the producer's 16,384 UTF-16-unit check; one in-flight operation/session; handshake 10 s; idle/write-stall 30 s. An oversized frontend frame is fatal `08P01`: close before allocation rather than attempt recovery across its unread body. Outbound DataRow frames may exceed that inbound cap but must fit the separately reserved 8 MiB complete encoded-result bound. Preserve producer QoS, scan/deadline and output bounds. Connection count must derive from existing foreground admission, not create a second unbounded pool. These provisional ceilings need owner review; they are not implemented settings. |
| Authentication | No new credential issuer, password database, trust/MD5 scheme, unauthenticated tenant choice or legacy-alias authorization path. Public HTTP SQL requires a **ts-auth project access token**, with signed project/database restrictions; `databases:read` alone does not authorize a legacy `tb_` key on this project route. A future TLS-only PasswordMessage envelope for that existing token is a candidate for auth-owner approval, not a new accepted credential type today. Reuse signature/type/issuer/audience/lifetime/authority/scope checks and catalog narrowing, per operation and on expiry; fail closed on unavailable authority. Startup user/database strings grant nothing. |

The .NET authentication handlers use HttpContext and HTTP headers; they cannot simply be called on a TCP session. Extracting or sharing a validated principal/grant boundary is an **auth-owner dependency**. Scope-only token decoding or a fabricated HttpContext cannot substitute for current middleware checks. The existing SHA-256 API-key lookup is not a SCRAM verifier; do not add SCRAM or derive verifiers without a separate ts-auth decision. This task creates and uses no authentication payloads; its examples are inert labels and bytes only.

Query execution is cooperative: `tb_qos_scan` checks cancellation per candidate and time every 32 candidates, charging 8 units/row plus 4 per JSON read. Native Cancel/timeout APIs do not provide a general interrupt. LIMIT alone cannot bound pre-result sorting, expression work or reader-disposal drain. Future cancellation and slow-client tests must prove worker/session cleanup, including work before the first row and after disconnect, without promising immediate cancellation of arbitrary native SQL.

`QueryService` already returns materialized, bounded HTTP results with `Truncated`. PostgreSQL CommandComplete has no equivalent truncation bit. R1 should **require an explicit LIMIT** within the producer cap, prevalidate the complete result/encoded bytes, and return `54000` before any rows for byte overflow or unexplained partial results. With an explicit LIMIT, exactly that many rows is normal SQL completion even if more matches exist; the producer's lookahead `Truncated=true` at that count is not an error. A truncation below the requested count is a resource error. Missing LIMIT gets `0A000` so the hidden default cannot produce misleading completion. Reserve both retained JSON and wire bytes, enforce an 8 MiB encoded-result cap, and release engine resources before sending to a slow client. This stricter transport contract is proposed, not an HTTP behavior change.

## Implementation phases

Estimates are planning ranges for one engineer with relevant protocol experience, excluding waits and dependency-owner work. They are not benchmarks or delivery commitments.

1. **P0: finish this offline artifact (current task).** Endpoint: local documentation commit, scoped diff and deterministic fixture checks. No PR, CI, server or production claim.
2. **P1: serial admission and contract decisions (1–3 engineer-days).** Reconcile TS-746 with the spike/archive; obtain the authoritative Rust contract; nominate transport owner and dependency owners. Decide selector/result shape, target clients, auth grant boundary, TLS routing and cancellation omission. Gate: approved compatibility/security contract and fixed resource policy; no release-critical writer displaced. Draft PR publication separately admitted.
3. **P2: admitted read-only R1 prototype (5–10 engineer-days after dependencies).** Implement only the reviewed subset adapter and bounded state machine; keep writes/transactions/extended execution unsupported. Future tests use real application integration, synthetic data and test-only ephemeral ports, parallel-safe in existing Fast/Slow suites. Gate: TLS refusal/downgrade and authorization-negative tests; parity with HTTP values and metering; complete framing/recovery tests; no customer-data/replication mutation; cancellation-token cleanup and slow-client admission evidence. No tests run or ports opened in this task.
4. **P3: admitted read-only extended R2 (10–20 additional engineer-days).** Add AST parameters, stable OIDs, bounded statements/portals, text formats and correct Execute/Sync recovery. Gate: pinned target-client integration for Parse/Bind/Describe/Execute/Close/Flush/Sync, empty results, NULL/numeric/Unicode, portal resumption and abuse. Explicit transactions require their own later snapshot/lifecycle admission. Mature library choice and native interoperability must be verified before adoption.
5. **P4: release consideration (2–5 engineer-days plus owner approvals).** Re-review security/cost, update API/protocol docs, run then-required formatting/build/analyzers/security/Fast checks before any push/release. Slow runs independently with visible failure reporting. Hosting/CI owners own rollout. R1/R2 never imply a write phase, global PostgreSQL isolation or general compatibility.

Every future integration gate must cover the exact commit advanced. Offline fixture checks cannot replace Fast or demonstrate TLS/auth/durability. No configuration flag is proposed: this study has no runtime path; a separately authorized future implementation follows the always-on repository policy after acceptance, unless a human explicitly changes that policy.

## Cost and security risks

| Risk | Practical cost / gate |
| --- | --- |
| Large compatibility surface | Prepared queries, startup SQL, catalogs and driver version changes can consume weeks beyond the narrow subset. Publish a tested client matrix; unsupported calls fail explicitly. Avoid an open-ended PostgreSQL-emulation commitment. |
| Resource exhaustion | Persistent sessions, TLS handshakes, JSON plus wire buffers, sort work and cooperative cancellation compete with replication/restore workers on existing 512 MiB/shared-CPU machines. 16 concurrent maximum 8 MiB results alone retain 128 MiB before encoding/TLS overhead. Use existing byte admission, fail early and measure before selecting capacity. No machine sizing/spend claimed here. |
| New exposure and credential custody | A new public TCP entry adds TLS downgrade, parser, token-in-password, session-expiry and logging risks. Auth/security owners must sign off on per-operation authority and revocation parity. Do not persist tokens or query values; bound metric label cardinality. |
| Proxy/native build alternative | A separate service adds maintenance, auth/session state and latency; linking Rust server crates adds build/dependency/audit burden. Stock trust auth and native mutations conflict with this product boundary. Neither alternative has been installed or benchmarked. |
| False consistency or retry claims | TCP failover loses session state; cross-region reads may lag. Do not map disconnects into write retries or synthesize commit/ACK success. Local native transaction features and experimental multi-process support are insufficient evidence. |

**Next admission action:** review P1's contract decisions. No implementation or publication is authorized by this artifact.
