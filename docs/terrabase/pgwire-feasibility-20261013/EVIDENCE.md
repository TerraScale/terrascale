# Source register

Use these exact pins when evaluating a claim in [README.md](README.md). **Observed** means read from source/package bytes during this task; **historical** means an existing report, not rerun; **proposed** means design requiring implementation and integration evidence.

## Producer and local scope

Repository: `https://github.com/TerraScale/terrascale`. Reviewed source commit: **`e30ffdc287530551b161b0130220be53d3c0475e`**. `git rev-parse HEAD` and `git rev-parse origin/main` both returned this value initially; `git status --short --branch` showed a clean task worktree. No claim that the cached remote-tracking ref is GitHub's latest state. Other worktrees were inspected only to locate references; their producers were not substituted for this one.

### Query behavior (observed)

| Evidence | Finding |
| --- | --- |
| [RunSqlEndpoint.cs, lines 10–28](https://github.com/TerraScale/terrascale/blob/e30ffdc287530551b161b0130220be53d3c0475e/src/TerraBase/Features/Management/Queries/RunSqlEndpoint.cs#L10-L28), [ProjectApiRoute.cs](https://github.com/TerraScale/terrascale/blob/e30ffdc287530551b161b0130220be53d3c0475e/src/TerraBase/Features/Management/ProjectApiRoute.cs#L14-L34) | Public `/v1/projects/{project}/databases/{database}/sql`, explicitly read-only, read scope, project-token identity. |
| [SqlSubset.cs, lines 16–140](https://github.com/TerraScale/terrascale/blob/e30ffdc287530551b161b0130220be53d3c0475e/src/TerraBase/Features/Management/Queries/SqlSubset.cs#L16-L140), [literals, lines 277–301](https://github.com/TerraScale/terrascale/blob/e30ffdc287530551b161b0130220be53d3c0475e/src/TerraBase/Features/Management/Queries/SqlSubset.cs#L277-L301) | AST allow-list, one SELECT, SQLite dialect, 16,384 .NET string units, default 100/max 1000 rows, no placeholders. Literals are int64/finite double/string/bool; NULL requires IS [NOT] NULL. Parser package SqlParserCS 0.6.5 in Directory.Packages.props. |
| [QueryService.cs](https://github.com/TerraScale/terrascale/blob/e30ffdc287530551b161b0130220be53d3c0475e/src/TerraBase/Features/Management/Queries/QueryService.cs#L19-L80), [read/result path](https://github.com/TerraScale/terrascale/blob/e30ffdc287530551b161b0130220be53d3c0475e/src/TerraBase/Features/Management/Queries/QueryService.cs#L235-L325), [IQueryService.cs](https://github.com/TerraScale/terrascale/blob/e30ffdc287530551b161b0130220be53d3c0475e/src/TerraBase/Features/Management/Queries/IQueryService.cs#L9-L27) | `write: false`, 8 MiB serialized HTTP result, LIMIT+1 lookahead, id/value rows, missing projected field null, read metering and query-shape recording. This is not general SQL execution. |
| [SqliteDatabaseStore.cs](https://github.com/TerraScale/terrascale/blob/e30ffdc287530551b161b0130220be53d3c0475e/src/TerraBase/Engine/Sqlite/SqliteDatabaseStore.cs#L260-L298), [SqliteStorageTransaction.Queries.cs](https://github.com/TerraScale/terrascale/blob/e30ffdc287530551b161b0130220be53d3c0475e/src/TerraBase/Engine/Sqlite/SqliteStorageTransaction.Queries.cs#L63-L82) | Read transactions set `PRAGMA query_only = 1`; engine-owned SQL is compiled from SqlQuery. Durable transactions use a different owner path. |
| [SqliteStorageTransaction.cs, budget](https://github.com/TerraScale/terrascale/blob/e30ffdc287530551b161b0130220be53d3c0475e/src/TerraBase/Engine/Sqlite/SqliteStorageTransaction.cs#L453-L542), [SqliteSession.cs](https://github.com/TerraScale/terrascale/blob/e30ffdc287530551b161b0130220be53d3c0475e/src/TerraBase/Engine/Sqlite/SqliteSession.cs#L29-L76) | Per-row callback cancellation and approximate work accounting, not native VM instruction counting/general interrupt. Existing endpoint prose still mentions SQLite instructions; source determines this artifact's claim. |

### Authentication, ownership and hosting (observed source, untested behavior)

| Evidence | Boundary |
| --- | --- |
| [UserAuthExtensions.cs, project checks](https://github.com/TerraScale/terrascale/blob/e30ffdc287530551b161b0130220be53d3c0475e/src/TerraBase/Features/UserAuth/UserAuthExtensions.cs#L73-L207), [UserAuthenticationHandler.cs](https://github.com/TerraScale/terrascale/blob/e30ffdc287530551b161b0130220be53d3c0475e/src/TerraBase/Features/UserAuth/UserAuthenticationHandler.cs#L14-L135) | Project route rejects authenticated non-project-token identities; signed project/database narrowing, scope, issuer, audience, lifetime, token type, signing keys and authority remain necessary. HttpContext-dependent middleware is not TCP authentication. |
| [ApiKeysExtensions.cs](https://github.com/TerraScale/terrascale/blob/e30ffdc287530551b161b0130220be53d3c0475e/src/TerraBase/Features/ApiKeys/ApiKeysExtensions.cs#L11-L32), [EngineApiKeyStore.cs](https://github.com/TerraScale/terrascale/blob/e30ffdc287530551b161b0130220be53d3c0475e/src/TerraBase/Features/ApiKeys/EngineApiKeyStore.cs#L15-L76) | Separate legacy API-key path, SHA-256 lookup, expiry; not SCRAM credential material. Hidden legacy aliases have exceptional policy checks; do not copy those into a new public transport. |
| [fly.toml](https://github.com/TerraScale/terrascale/blob/e30ffdc287530551b161b0130220be53d3c0475e/src/TerraBase/fly.toml#L22-L39), [region-identity.md](https://github.com/TerraScale/terrascale/blob/e30ffdc287530551b161b0130220be53d3c0475e/docs/terrabase/region-identity.md) | HTTP service and HTTP pin/replay contract. No PostgreSQL TLS service is established by these files. Fly PostgreSQL-specific handler availability/behavior was not verified. |
| [Root rules](https://github.com/TerraScale/terrascale/blob/e30ffdc287530551b161b0130220be53d3c0475e/AGENTS.md), [TerraBase rules](https://github.com/TerraScale/terrascale/blob/e30ffdc287530551b161b0130220be53d3c0475e/src/TerraBase/AGENTS.md), [multi-region-replication.md](https://github.com/TerraScale/terrascale/blob/e30ffdc287530551b161b0130220be53d3c0475e/docs/terrabase/multi-region-replication.md) | Single service binary, engine interfaces, byte admission, automatic serving regions, auth-owner and replication-owner boundaries. Designs are not integration proof of every invariant. |
| [Decision 0010](https://github.com/TerraScale/terrascale/blob/e30ffdc287530551b161b0130220be53d3c0475e/docs/terrabase/decisions/0010-write-wait-and-synced-durable-ack.md#L1-L6), [SqliteSession settings](https://github.com/TerraScale/terrascale/blob/e30ffdc287530551b161b0130220be53d3c0475e/src/TerraBase/Engine/Sqlite/SqliteSession.cs#L78-L99) | 0010 explicitly marks write-wait superseded by design C. Keep local writes, durable applies and ACK semantics under existing owners; no added guarantee in pg-wire docs. |

## Turso integration and native provenance

Version **v0.8.2**, upstream commit **`5c168be5ef6a32ac966daf5983502a4f694b37ad`**. This actual pin is different evidence from the spike's earlier `120511cb` source review.

| Producer evidence | Observed |
| --- | --- |
| [UPSTREAM.md](https://github.com/TerraScale/terrascale/blob/e30ffdc287530551b161b0130220be53d3c0475e/src/vendor/Turso.Data.Sqlite/UPSTREAM.md), [patches](https://github.com/TerraScale/terrascale/tree/e30ffdc287530551b161b0130220be53d3c0475e/src/vendor/Turso.Data.Sqlite/patches) | C# source imported from pin, patched 0001–0004 for failed-statement cleanup and transaction probing. Historical regression results are not rerun here. |
| [TerraBase.csproj](https://github.com/TerraScale/terrascale/blob/e30ffdc287530551b161b0130220be53d3c0475e/src/TerraBase/TerraBase.csproj#L69-L70), [vendor build targets](https://github.com/TerraScale/terrascale/blob/e30ffdc287530551b161b0130220be53d3c0475e/src/vendor/Turso.Data.Sqlite/Directory.Build.targets#L12-L15), [Directory.Packages.props](https://github.com/TerraScale/terrascale/blob/e30ffdc287530551b161b0130220be53d3c0475e/Directory.Packages.props#L69-L70) | Project references for provider, central native package 0.8.2. No server library was found in central packages or provider references. |
| [Turso.Raw lock](https://github.com/TerraScale/terrascale/blob/e30ffdc287530551b161b0130220be53d3c0475e/src/vendor/Turso.Data.Sqlite/locks/Turso.Raw.packages.lock.json#L5-L10) | Resolved native 0.8.2, contentHash `3ReOw1hTID2OmhQ3PNO3Vyk5DQ7iyPoXSLy8VeMgiaqTefUiTUFNmz7WpMyKTbno9y859nFXy0DBV0suPnaVbg==`. |
| [TursoInterop.cs](https://github.com/TerraScale/terrascale/blob/e30ffdc287530551b161b0130220be53d3c0475e/src/vendor/Turso.Data.Sqlite/src/Turso.Raw/TursoInterop.cs), [TursoBindings.cs](https://github.com/TerraScale/terrascale/blob/e30ffdc287530551b161b0130220be53d3c0475e/src/vendor/Turso.Data.Sqlite/src/Turso.Raw/Public/TursoBindings.cs) | Database/connection/statement/value/UDF ABI; no pg-wire listener API. Status named Interrupt is not an exported interrupt function. Native enum numbers and managed TursoValueType enum numbers differ; do not use either as PostgreSQL OIDs. |
| Cached `~/.nuget/packages/turso.data.native/0.8.2` | Nuspec repository commit equals upstream pin. Linux x64 `libturso_sdk_kit.so`: **72,601,360 bytes**, SHA-256 **`56378e447d2efef78483d14eaf1e1972e567c44abb7b5238f592939903ed7fb4`**. `nm -D --defined-only`: 223 exports, 77 `turso_*`; zero pgwire/postgres/listen/server/interrupt-name matches. No library was loaded, restored or rebuilt. |

Existing [SECURITY-AUDIT.md](https://github.com/TerraScale/terrascale/blob/e30ffdc287530551b161b0130220be53d3c0475e/src/vendor/Turso.Data.Sqlite/SECURITY-AUDIT.md) is historical provenance/security context. Package signature, dependency advisories, all RIDs and bit-for-bit native reproducibility were not audited again. No broad “secure” claim is derived from export names.

### Exact upstream primary sources

All Turso links below are fixed at the commit, not `main`.

| Primary source | Relevant observation |
| --- | --- |
| [sdk-kit/Cargo.toml](https://github.com/tursodatabase/turso/blob/5c168be5ef6a32ac966daf5983502a4f694b37ad/sdk-kit/Cargo.toml), [SDK C ABI](https://github.com/tursodatabase/turso/blob/5c168be5ef6a32ac966daf5983502a4f694b37ad/sdk-kit/src/capi.rs) | C ABI crate dependencies/exports do not include the separate PostgreSQL server crate. |
| [postgres/server/Cargo.toml](https://github.com/tursodatabase/turso/blob/5c168be5ef6a32ac966daf5983502a4f694b37ad/postgres/server/Cargo.toml), [Cargo.lock](https://github.com/tursodatabase/turso/blob/5c168be5ef6a32ac966daf5983502a4f694b37ad/Cargo.lock#L4388-L4392), [postgres/cli](https://github.com/tursodatabase/turso/blob/5c168be5ef6a32ac966daf5983502a4f694b37ad/postgres/cli/Cargo.toml#L1-L26) | `turso_pg_server`, pgwire requirement 0.36 / resolved 0.36.3, separate `tursopg` executable. |
| [postgres/server/lib.rs](https://github.com/tursodatabase/turso/blob/5c168be5ef6a32ac966daf5983502a4f694b37ad/postgres/server/lib.rs#L55-L79), [startup/query](https://github.com/tursodatabase/turso/blob/5c168be5ef6a32ac966daf5983502a4f694b37ad/postgres/server/lib.rs#L156-L241), [parameters](https://github.com/tursodatabase/turso/blob/5c168be5ef6a32ac966daf5983502a4f694b37ad/postgres/server/lib.rs#L455-L489) | TCP host, no supplied TLS acceptor, NoopHandler startup, direct native prepare/mutation, ignored Execute `_max_rows`, text parameter decoding and NUMERIC→f64. Rows collected before returning; `error_info` at lines 758–760 uses XX000. |
| [Postgres compatibility](https://github.com/tursodatabase/turso/blob/5c168be5ef6a32ac966daf5983502a4f694b37ad/postgres/COMPAT.md#L17-L32), [protocol table](https://github.com/tursodatabase/turso/blob/5c168be5ef6a32ac966daf5983502a4f694b37ad/postgres/COMPAT.md#L586-L592) | Separate PostgreSQL grammar/frontend; documented partial behavior, silently ignored clauses, plaintext/trust auth, no portal suspension, cancellation or COPY protocol. Not a claim of TerraBase compatibility. |
| [README](https://github.com/tursodatabase/turso/blob/5c168be5ef6a32ac966daf5983502a4f694b37ad/README.md#L42-L82), [manual](https://github.com/tursodatabase/turso/blob/5c168be5ef6a32ac966daf5983502a4f694b37ad/docs/manual.md#L95-L156) | Experimental PostgreSQL frontend and multi-process WAL support; manual's experimental coordination option is explicitly not production-ready while old limitations contradict it. No current TerraBase integration demonstrated. |

## PostgreSQL specification pins

Protocol reference: **PostgreSQL 18**, including its protocol **3.2** changes. Design target: **3.0** fallback. Source release: **REL_18_0**, resolved by public `git ls-remote` to **`3d6a828938a5fa0444275d3d2f67b64ec3199eb7`**. Versioned HTML was retrieved on 2026-10-13; `/docs/18/` pages can evolve with minor releases. Exact release-source links below fix protocol/type assertions independently of those pages. No running PostgreSQL instance was queried.

### Protocol, errors and isolation

| Primary reference | Assertions used |
| --- | --- |
| [Overview §54.1](https://www.postgresql.org/docs/18/protocol-overview.html), [message types](https://www.postgresql.org/docs/18/protocol-message-types.html) | Typed versus initial framing, separate simple/extended cycles, network byte order and format codes; 3.0/3.2 distinction. |
| [Flow §54.2](https://www.postgresql.org/docs/18/protocol-flow.html), [exact release protocol source](https://github.com/postgres/postgres/blob/3d6a828938a5fa0444275d3d2f67b64ec3199eb7/doc/src/sgml/protocol.sgml) | SSL one-byte reply/handshake, GSSENC/direct TLS and ALPN, version negotiation, Sync recovery, implicit transactions, cancellation on separate connection, missing cancellation key in some third-party backends. |
| [Message formats §54.7](https://www.postgresql.org/docs/18/protocol-message-formats.html) | Startup, SSL/GSSENC/cancel codes; 3.2 variable cancel-key length versus 3.0 4-byte key; Parse/Bind/Execute/Describe/Close/Flush/Sync; DataRow null sentinel and RowDescription; Ready I/T/E. |
| [Error fields §54.8](https://www.postgresql.org/docs/18/protocol-error-fields.html), [SQLSTATE Appendix A](https://www.postgresql.org/docs/18/errcodes-appendix.html), [exact error-code source](https://github.com/postgres/postgres/blob/3d6a828938a5fa0444275d3d2f67b64ec3199eb7/src/backend/utils/errcodes.txt) | ErrorResponse S/V/C/M fields, stable SQLSTATE names/codes. Mapping TerraBase failures to these codes is a proposal, not PostgreSQL-mandated TerraBase behavior. |
| [Transaction isolation §13.2](https://www.postgresql.org/docs/18/transaction-iso.html) | PostgreSQL isolation levels have defined visibility/anomaly semantics. No evidence equates TerraBase local read transactions to global PostgreSQL guarantees. |

### Value and type primary sources

| Primary reference | Assertions used |
| --- | --- |
| [pg_type.dat at exact release](https://github.com/postgres/postgres/blob/3d6a828938a5fa0444275d3d2f67b64ec3199eb7/src/include/catalog/pg_type.dat) | bool 16, bytea 17, int8 20, int2 21, int4 23, text 25, json 114, float8 701, numeric 1700, jsonb 3802; type lengths. |
| [Numeric types §8.1](https://www.postgresql.org/docs/18/datatype-numeric.html), [numeric_send source](https://github.com/postgres/postgres/blob/3d6a828938a5fa0444275d3d2f67b64ec3199eb7/src/backend/utils/adt/numeric.c#L1163-L1186) | NUMERIC precision differs from floating point; its binary representation has digits/weight/sign/dscale, not a double. |
| [int8send](https://github.com/postgres/postgres/blob/3d6a828938a5fa0444275d3d2f67b64ec3199eb7/src/backend/utils/adt/int8.c#L94-L103), [float8send](https://github.com/postgres/postgres/blob/3d6a828938a5fa0444275d3d2f67b64ec3199eb7/src/backend/utils/adt/float.c#L567-L577), [boolsend](https://github.com/postgres/postgres/blob/3d6a828938a5fa0444275d3d2f67b64ec3199eb7/src/backend/utils/adt/bool.c#L188-L198) | Binary scalar examples in fixtures. Not R1 feature support. |
| [JSON types §8.14](https://www.postgresql.org/docs/18/datatype-json.html), [json_send](https://github.com/postgres/postgres/blob/3d6a828938a5fa0444275d3d2f67b64ec3199eb7/src/backend/utils/adt/json.c#L138-L147) | json text preservation versus jsonb behavior; escaped Unicode/NUL distinctions; raw json binary text has no jsonb version prefix. |

## Existing design/owner search evidence

1. `rg -n -i 'pg.?wire|postgres.?wire|TS-746|wire protocol' .` found the spike's pg-wire row and launch exclusion, not a current implementation. Local `git log --all` matching pgwire/pg-wire/Postgres wire/TS-746 found no matching commit messages. Ref search found only this task branch and the adjacent import refs.
2. Historical [turso-spike.md](https://github.com/TerraScale/terrascale/blob/e30ffdc287530551b161b0130220be53d3c0475e/docs/terrabase/turso-spike.md#L107-L126) and [later swap notes](https://github.com/TerraScale/terrascale/blob/e30ffdc287530551b161b0130220be53d3c0475e/docs/terrabase/turso-spike.md#L283-L308) were read. No listed spike performance/functionality result was reproduced.
3. Cached import refs inspected: `origin/feat/postgres-turso-import-core` **`62f251f6beb8c4075dbba6f59a6a97c66487b754`**, engine **`567bfd33fe1257a922c4f8df4e8217517fc06268`**. [Import contract at core pin](https://github.com/TerraScale/terrascale/blob/62f251f6beb8c4075dbba6f59a6a97c66487b754/docs/terrabase/import.md) says no direct source connections/credentials and names existing implementation work. It is not in this reviewed producer; no PR was opened or worked on here.
4. Accessible archive: sibling `ts-resources` HEAD **`f3b54ea3dcc1d81cb13b5fa35645fb234286fc1b`**, clean inspected file `archive/TerraBaseV2/docs/parity/ps-04-transfer-adapters-sweep.md`, lines 111–112, read also via `git show` at that pin. PB-1023/1024 record accepted differences for PostgreSQL wire/admin. This is old V2 context, not a current owner assignment.
5. The required Rust sibling is absent at `../terrabase-rust` and `/home/mariogk/Projects/TerraScale/terrabase-rust`. Searches found historical scratch/vendor copies and other products' pgx clients, not an authoritative current pg-wire owner. Public primary documentation reads were unauthenticated; no Ku/production-service call or live GitHub assignment check occurred.

## Selected retrieved-source fingerprints

These SHA-256 hashes identify downloaded bytes used in this task; they are not upstream signature verification. Public reads used only PostgreSQL documentation and raw source URLs. Source responses are held in disposable scratch storage, not added as vendored code.

| Source at pin above | SHA-256 |
| --- | --- |
| Turso `sdk-kit/Cargo.toml` | `d99bad1e307c289349587cc858728288ddc56d9e6f62224dbafba78ed5361185` |
| Turso `postgres/server/lib.rs` | `d2e7cbd64387f4a8d3541b81eb520ca41b1e31fb34f358b092a297bb7924b35a` |
| Turso `postgres/COMPAT.md` | `b0e182363b281dac2c083b6b07354825a36cb7507ce107f607bc4c15e3a71dce` |
| PostgreSQL `doc/src/sgml/protocol.sgml` | `a71afc914d8f0cdba4cea568d4cb94b4bd80a019d368a89a8644e81c1409a82f` |
| PostgreSQL `src/backend/utils/errcodes.txt` | `6e8de346643ba84aa3c9c6a73360acfc7b2dfb89162c06c08ce9bf5bcd5bbcba` |

Unverified: executing either server/provider behavior, client interoperability, TLS on Fly, cancellation routing, the authoritative Rust contract, current TS-746 assignment, complete native build provenance and future cost under load. Proposed limits, output OIDs, auth envelope and implementation estimates are not producer facts.
