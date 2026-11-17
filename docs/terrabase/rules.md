# TerraBase Rules: end-user reads and writes

Status: Partly built. The rules set, deploy checks, the condition compiler, versions, restore, the section 7 routes,
end-user reads (section 4: shapes and the row read), the section 5 row writes and the section 8 simulator are live.
Rules replaced read grants and the `tables` claim of [scoped end-user tokens](../auth/scoped-end-user-tokens.md).

A project's end users sign in with the project's own ts-auth tenant and read and write a database straight from the
browser, with no developer backend. The database owner deploys one **rules** set per database: for each table,
who may read, create, update and delete which rows and columns, and what an incoming row must look like. TerraBase
checks the end user's token offline and evaluates the rules on every request: reads in the answering region, writes
inside the home region's write transaction. A developer backend that keeps its own users mints a **custom token**
instead; rules treat both the same way.

Project credentials (API keys, project tokens, platform sign-in tokens) are not limited by rules, as with grants
today. End-user SQL stays refused.

## 1. Rules

One rules set per database, sent as JSON. A database without one denies every end-user request.

```json
{"tables":{
  "todos":{
    "read":"row.owner_id = auth.uid",
    "create":"auth.is_anonymous = 0 OR new.shared = 0",
    "update":"row.owner_id = auth.uid",
    "delete":"row.owner_id = auth.uid",
    "columns":["id","owner_id","title","done","shared","created_at","updated_at"],
    "writable":["title","done","shared"],
    "validate":["length(new.title) BETWEEN 1 AND 200","new.done IN (0, 1)"],
    "set":{"create":{"id":"request.ulid","owner_id":"auth.uid","created_at":"request.time"},
           "update":{"updated_at":"request.time"}}}}}
```

| Field | Value | Missing |
|---|---|---|
| `read`, `create`, `update`, `delete` | One condition (section 2) | The operation is denied |
| `columns` | 1 to 256 column names an end user receives | Every stored column |
| `writable` | 0 to 256 column names an end user may set on create and update | No column is writable |
| `validate` | 0 to 32 conditions, each over `new.*`, `auth.*` and `request.*`, all of which must hold after a create or update | No check |
| `set.create`, `set.update` | Column → server value: `auth.uid`, `auth.claims.<name>`, `auth.org_id`, `auth.org_role`, `auth.is_anonymous`, `request.time`, `request.ulid` (create only) or a literal | Nothing is filled |

- A condition is allowed only when it evaluates to true. `"read":"TRUE"` lets every signed-in end user, guests
  included, read the table; `"read":"auth.is_anonymous = 0"` excludes guests.
- `set` overrides the client: a `set` column cannot also be in `writable`, and a request naming it answers
  `403 column_not_writable`. `set.update` runs on every update, including one that changes no other column.
- The row id alias (an `INTEGER PRIMARY KEY` column) may be in `writable` for create only; an update never changes
  the row id.
- An ownership column in `writable` needs `new.<column> = auth.uid` in both `create` and `update`, or a user can create
  rows for others and hand their rows over. Prefer `set.create` as in the example. Deploy reports a writable column
  that `read` compares with `auth.uid` and that `create` or `update` leaves unchecked as the warning
  `unchecked_owner` in `warnings`, which does not block the deploy.
- Limits: 256 tables, 4096 characters per condition, 256 KiB per rules set.

### Deploy checks

`PUT` compiles every condition against the current schema before it saves. Any problem answers
`400 invalid_rules` with every problem in `details.problems`:

```json
{"error":{"code":"invalid_rules","message":"…","details":{"problems":[
  {"table":"todos","field":"validate[1]","code":"unknown_column","message":"new.done_at: the table has no column done_at"}]}}}
```

Problem codes: `unknown_table`, `unknown_column`, `unsupported_table` (`WITHOUT ROWID` and virtual tables),
`unsafe_where` (section 2), `reference_not_allowed` (for example `row.*` in `create` or `validate`),
`column_conflict` (a column in both `writable` and `set`), `invalid_value` (a `set` value that is not one of the
allowed references or literals).

A later DDL can make a deployed rule stop fitting its table. Each request compiles the rule it needs against the
schema it reads; a rule that no longer compiles denies with `403 rules_invalid`, and `GET …/rules` lists the same
problems under `problems` so the dashboard can show them. Deploy again after the DDL.

## 2. Condition language

Conditions use the shape `where` language of [shape reads](shape-reads.md) section 2a, validated by the same code
(`src/TerraBase/Features/Shapes/ShapeFilter.cs`): literals, operators, `CASE`, `CAST`, `COLLATE`, deterministic
functions, no subqueries. Rules change two things. Column names are always qualified (`row.title`, `new.title`;
a bare name answers `unsafe_where`), and these typed references are added:

| Reference | Type | Value |
|---|---|---|
| `auth.uid` | TEXT | The token's `sub`, never NULL |
| `auth.claims.<name>` | TEXT, INTEGER, REAL, or a list | The trusted claim `<name>` (section 3.3); NULL when absent |
| `auth.org_id` | TEXT | The selected project organization; NULL when none |
| `auth.org_role` | TEXT | `admin` or `member`; NULL when no organization is selected |
| `auth.is_anonymous` | INTEGER | 1 for a guest, otherwise 0 |
| `row.<column>` | column | The stored row, before the write |
| `new.<column>` | column | The row as stored after the write |
| `request.time` | INTEGER | Server time in Unix milliseconds, one value per request |
| `request.ulid` | TEXT | A new ULID; only as a `set.create` value |

Where each reference is allowed:

| Rule | `row.*` | `new.*` | `auth.*` | `request.time` |
|---|---|---|---|---|
| `read` | yes | no | yes | no |
| `create` | no | yes | yes | yes |
| `update` | yes | yes | yes | yes |
| `delete` | yes | no | yes | yes |
| `validate` | no | yes | yes | yes |

`read` cannot use `request.time` (`reference_not_allowed`). Shape snapshots and chunks are sealed, cached and shared
per handle and change only when rows change, so a row whose visibility depends only on the clock
(`row.expires_at > request.time`) would keep being served after it should be hidden. Hide expired rows by deleting
or updating them.

- **Binding.** Every `auth.*` and `request.*` value, and every `row.*` value the server read, is bound as a statement
  parameter. Nothing from a token or a request body is spliced into SQL text. Parameters written by the author
  (`:x`, `$x`, `?`, and today's `:sub`) answer `unsafe_where`.
- **Truth.** A condition compiles to `((condition) IS TRUE)`: NULL and 0 deny.
- **NULL and missing claims.** A missing claim, organization or column value is NULL, so a comparison with it is
  NULL and denies, and so does its negation: `NOT (auth.claims.banned = 1)` denies when `banned` is absent. Only
  `IS`, `IS NOT`, `coalesce` and `ifnull` can turn NULL into true; the author writes them on purpose.
- **Lists.** A claim reference that is the right operand of `IN` or `NOT IN` (`'editor' IN auth.claims.roles`) is
  in list position and compiles to a membership test over the bound JSON array; every other claim reference is in
  scalar position. `IN (` followed by a claim among other values answers `unsafe_where` at deploy.
- **Claim types at runtime.** A claim's type is known only per token. A missing list claim, or a scalar claim in list
  position, makes `IN` and `NOT IN` NULL, so both deny: `'banned' NOT IN auth.claims.flags` denies when `flags` is
  absent. A list claim in scalar position binds NULL. A present empty list makes `IN` false and `NOT IN` true.
- **Claim names** match `[a-z][a-z0-9_]{0,62}`.

## 3. Identity

### 3.1 Tenant tokens (direct sign-in)

Only end-user sign-in access tokens are end users: those with `token_use: user`
([project tenant management](../auth/project-tenant-management.md): hosted, social and magic-link sign-in, guests
from [consumer integration](../auth/consumer-integration.md),
[project organizations](../auth/project-organizations.md)), and custom tokens (`token_use: custom`, section 3.2).
Every such token also names the project's data audiences, one per TerraBase API origin `A` in ts-auth's
`TS_AUTH_ACCESS_TOKEN_AUDIENCES`: `{A}/v1/projects/{project}`, for example
`https://gru.api.terrascale.tech/v1/projects/{project}`. No client setting turns this on or off: a token reaches no
row until the database's rules allow it, and a database without rules allows nothing. ID tokens never work, and
impersonation tokens (an admin acting as a user) carry neither the data audiences nor `claims`.

The tenant's machine-client tokens (`token_use: machine`, `sub` = `client_id`, 5 to 60 minutes,
`src/TerraScale.Authentication/Features/Oidc/MachineClients.cs`) are not end users. ts-auth refuses a data audience
in a machine client's `allowed_audiences` (`400 invalid_request`), and TerraBase refuses a machine token that names
one anyway (rule 4). A machine needing data uses a project API key.

TerraBase accepts a tenant token offline when:

1. The unverified `iss` is exactly `{O}/{P}`, where `O` is the origin of TerraBase's configured `AuthIssuer`
   (`https://auth.terrascale.tech` in production) and `P` a canonical ULID equal to the route's `{project}`. `terrascale` is never a project id, so a platform token cannot pass as a tenant token, and a token of
   project A answers `401` on project B's routes: the issuer is bound to the route before any key is fetched.
2. Its signature verifies against `P`'s own JWKS (`{iss}/.well-known/jwks.json`). TerraBase fetches it only for a
   project its catalog knows, caches it per project like the platform ring today (`IIssuerKeys`), and refreshes
   once on an unknown `kid`. Keys of one tenant never verify another tenant's tokens. A deleted project's JWKS
   answers `404` and its keys are dropped.
3. `typ` is `at+jwt`, `alg` `ES256`, `aud` contains `{A}/v1/projects/{P}` for one of TerraBase's own configured
   `AuthAudiences` `A` (exact match), `exp` is in the future (30-second skew, as today), and `sub` is a canonical ULID.
4. `token_use` is `user` or `custom`; any other value, or none, answers `401`. Its `scope`, when present, holds only
   the OpenID sign-in scopes `openid`, `email`, `profile` and `offline_access` (ts-auth's userinfo reads them); any
   other scope answers `401`. It carries no `project_id`, `instance_id`, `token_kind`, `platform_role`, `key_id` or
   IP-allowlist claim; any of them answers `401`.

Guests (`is_anonymous: true`) are accepted, and rules see `auth.is_anonymous = 1`. A request with no token is never
an end user; it answers `401`.

### 3.2 Custom tokens (backend minting)

A backend that keeps its own users mints a custom token:

```http
POST https://auth.terrascale.tech/v1/{project}/custom-tokens
Authorization: Bearer <project API key with auth:tokens:mint>

{"user_id":"01J…","database":"01J…","claims":{"plan":"pro","roles":["editor"]}}
```

```json
{"access_token":"<JWT>","token_type":"Bearer","expires_in":300}
```

- The key needs only the new scope `auth:tokens:mint` (today `sql:read` and `sql:write`). It belongs to the route's
  project and has no database restriction. A mint-only key can neither run SQL nor manage ts-auth.
- `user_id` must be a canonical uppercase ULID, as on main today; a backend whose users have other IDs keeps a map.
- The token is signed by the project tenant's key ring under the tenant issuer, with `token_use: custom`,
  `aud` the data audience only (so ts-auth's OAuth endpoints refuse it), `sub` = `user_id`, `claims` as given
  (section 3.3 limits) and `database` when given. It lives at most 5 minutes; the backend mints again.
- `database` restricts the token to that database: any other answers `404 unknown_database`. Custom tokens carry no
  organization, so `auth.org_id` and `auth.org_role` are NULL, and `auth.is_anonymous` is 0.
- The backend vouches for `user_id`; it may name a tenant user or not. A mint key can therefore act as any end user
  of the project, like a server SDK credential. Keep it on the backend.
- `POST /v1/terrascale/projects/{project}/end-user-tokens`, its `tables` field and its top-level `roles` claim are
  deleted. Roles become the trusted claim `claims.roles`.

### 3.3 Trusted claims

ts-auth gains a user field `custom_claims`, separate from the profile metadata of [user profiles](../auth/user-profiles.md):

- Set by `PATCH /v1/{project}/users/{user}` `{"custom_claims":{…}}` with `auth:write` or a project admin; the object
  replaces the previous one whole, `null` clears it. The user's own `/me/profile` cannot change it, and
  `unsafe_metadata` never reaches a rule.
- A JSON object of at most 32 members and 1000 bytes serialized. Names match `[a-z][a-z0-9_]{0,62}`. Values are
  strings (at most 256 characters), integers, finite numbers, booleans (bound as 1 or 0), or arrays of at most 64
  strings or numbers. Anything else answers `400 invalid_request`.
- ts-auth copies it into every access token it issues for the user as `claims`, except impersonation tokens; the
  change reaches TerraBase at the
  user's next refresh. Claims are visible to the user who holds the token; never put a secret in them.

### 3.4 What rules see

| Reference | Tenant token | Custom token |
|---|---|---|
| `auth.uid` | `sub` | `sub` (the minted `user_id`) |
| `auth.claims.<name>` | `claims.<name>` from `custom_claims` | `claims.<name>` from the mint body |
| `auth.org_id`, `auth.org_role` | `org_id`, `org_role` | NULL |
| `auth.is_anonymous` | `is_anonymous` (absent is 0) | 0 |

### 3.5 Revocation

Tokens are checked offline, so a token keeps working until it expires. Each limit below is the access-token lifetime
(5 minutes) plus TerraBase's 30-second skew:

| Change | Takes effect on TerraBase |
|---|---|
| Rules deploy, writes | The next request: writes read the rules in their own home transaction |
| Rules deploy, reads | When replication brings the new rules to the answering region, after that region's replica lag; every shape handle then changes (section 4.3) |
| User disabled or deleted, signed out everywhere, session revoked | Within 5 minutes 30 seconds (refresh is refused at once) |
| Organization membership removed or role changed | Within 5 minutes 30 seconds |
| `custom_claims` changed | At the next refresh, within 5 minutes 30 seconds |
| Mint key revoked | No new custom tokens at once; issued ones within 5 minutes 30 seconds |
| Project deleted | Its databases are deleted; its JWKS answers `404` |

Three more limits on what a revoked user keeps:

- **Epoch keys.** An epoch key decrypts every object of its handle sealed in its 10-minute epoch
  ([shape reads](shape-reads.md) section 10), and the CDN serves ciphertext without checking tokens
  ([live shapes](free-reads-live.md)). A user whose token stops working can still decrypt the chunks of their handle
  sealed until the end of the epoch of their last key fetch: up to 10 minutes after the token's last use. A rules
  deploy changes the handle, so chunks sealed after it use keys the old handle never gets.
- **Live long-polls.** A `live=true` request admitted before `exp` may wait its full 20 seconds and answer after
  `exp`. A hints socket closes at `exp`.
- **Already served.** Rows, epoch keys and snapshots a client already received stay with it. Rules decide what is
  served next; they cannot take back what was served.

## 4. Reads

### 4.1 Routes

| Route | Rule | Where it runs |
|---|---|---|
| `GET …/tables/{table}/shape`, `…/shape/keys`, `…/shape/manifest`, live and hints | `read` + `columns` | The answering region, on its replica |
| `GET /v1/projects/{project}/databases/{database}/tables/{table}/rows/{key}?columns=` | `read` + `columns` | The answering region, on its replica |

`{key}` is the row's key as shapes report it (the row id as a decimal string). The row read answers
`200 {"row":{…}}` with the readable columns, or `404 row_not_found` when the row is missing or `read` is false for
it, so a hidden row and a missing one look the same. It counts as one SQL read in the
[operation allowance](operation-allowance.md); shape reads stay free.

The read runs on one replicated revision: the rules and the rows it reads come from the same pages, so a replica
never applies newer rules to older rows or the reverse.

### 4.2 Filters and columns

For an end user:

1. The effective condition is `(read) AND (request where)`, each validated on its own first, as grants do today.
2. The request `where` may name only readable columns (`columns`). Naming any other answers
   `403 column_not_readable` with `details.column`, so filtering on a hidden column cannot reveal its values.
3. The request `columns` must be a subset of `columns`; naming any other answers `403 column_not_readable`.
   Without `columns`, the shape carries every readable column.

### 4.3 Handles, caches and keys

The shape handle gains two fields (`ShapeHandle.cs`):

```
fields = … ‖ filter ‖ 0x00 ‖ rules_version ‖ 0x00 ‖ binding
```

- `rules_version` is the deployed version's ULID; empty for project credentials.
- `binding` is the canonical JSON of the `auth.*` values the table's `read` rule references, sorted by name; empty
  when it references none. Two users whose rule binds different values never share a handle, a cached body, a sealed
  chunk or an epoch key; a rule that binds nothing (`"read":"TRUE"`) keeps one shared handle.
- Auth values are bound as statement parameters, so the filter text is the same for every user; the binding in the
  handle is what keeps users apart.
- The domain becomes `TB-SHAPE-HANDLE-V3` and the protocol field `"3"`, so every existing handle answers
  `409 must_refetch` once.
- A deploy changes `rules_version`, so every open shape answers `409 must_refetch` at its next request; a live
  request waiting at the deploy is answered `409` when the rules change reaches its region, and the hints socket
  closes. A hints socket also closes at its token's `exp`.
- The server always computes the handle from the caller's own token and the current rules. A handle is never a
  capability: a client sending another user's handle gets `409 must_refetch` with its own, and `/shape/keys`
  releases epoch keys only for the handle it computed.
- The live handle-resolution cache ([live shapes](free-reads-live.md)) keys on the full query, `rules_version` and
  `binding` in place of the bound grant. Origin answers keep `Cache-Control: private` and `Vary: Authorization`; the
  CDN holds only sealed ciphertext, as today.

## 5. Writes

### 5.1 Routes

| Route | Body | Answer |
|---|---|---|
| `POST /v1/projects/{project}/databases/{database}/tables/{table}/rows` | `{"row":{…}}` | `201 {"key":"…","row":{…} or null}` |
| `PATCH …/tables/{table}/rows/{key}` | `{"row":{…}}` (only the columns to change) | `200 {"key":"…","row":{…} or null}` |
| `DELETE …/tables/{table}/rows/{key}` | none | `204` |
| `POST /v1/projects/{project}/databases/{database}/rows:batch` | `{"operations":[…]}` | `200 {"results":[…]}` |

- Values use the shape body's JSON encoding ([shape reads](shape-reads.md) section 6): null, numbers, strings, and
  blobs as `{"base64":"…"}`. Bodies are UTF-8 JSON of at most 1 MiB.
- An answer's `row` is the stored row cut to `columns`, or `null` when `read` is false for it.
- A batch holds 1 to 100 operations, each `{"op":"create","table":"…","row":{…}}`,
  `{"op":"update","table":"…","key":"…","row":{…}}` or `{"op":"delete","table":"…","key":"…"}`, across any
  tables of the database. It runs in one transaction, in order, all or nothing; the first failure rolls everything
  back and answers that operation's error with `details.index`.
- Writes count against the database's monthly write allowance like SQL writes ([operation allowance](operation-allowance.md))
  and share its [resource admission](qos.md).

### 5.2 Evaluation

Every end-user write runs in the database's home region, inside one write transaction that also reads the rules.
The database's writer runs one write transaction at a time, so nothing commits between the check and the write, and
a rules deploy (itself a home write) is ordered entirely before or after it. Before the transaction, each operation
fails fast: no rule for the operation → `403 rule_denied`; a body column that is unknown → `400 unknown_column`,
in `set` or not in `writable` → `403 column_not_writable`; the row id alias on update → `400 invalid_request`.

**Create**

1. `INSERT OR ABORT` the body's columns plus `set.create`, `RETURNING rowid`. A UNIQUE or primary-key conflict
   answers `409 row_exists`, with `details.key` when the caller may read the conflicting row; a NOT NULL, CHECK or
   foreign-key failure answers `400 constraint_failed`.
2. `SELECT (create) IS TRUE, (validate[0]) IS TRUE, … FROM t WHERE rowid = $rowid`, with `new.x` compiled to the
   stored column `x`.
3. `create` false → roll back, `403 rule_denied`; a `validate[i]` false → roll back, `400 validation_failed` with
   `details.clause = i`; otherwise commit.

**Update**

1. `SELECT <every column>, (read) IS TRUE FROM t WHERE rowid = $key`. No row, or `read` false → `404 row_not_found`.
2. `UPDATE OR ABORT t SET <body columns, set.update> WHERE rowid = $key`. Conflicts answer as on create.
3. `SELECT (update) IS TRUE, (validate[0]) IS TRUE, … FROM t WHERE rowid = $key`, with `row.x` bound to the values
   step 1 read and `new.x` compiled to the stored column `x`.
4. `update` false → roll back, `403 rule_denied`; a `validate[i]` false → roll back, `400 validation_failed`;
   otherwise commit.

**Delete**

1. `SELECT (read) IS TRUE, (delete) IS TRUE FROM t WHERE rowid = $key`. No row, or `read` false →
   `404 row_not_found`; `delete` false → `403 rule_denied`.
2. `DELETE FROM t WHERE rowid = $key`; commit.

`OR ABORT` overrides any `ON CONFLICT REPLACE` or `IGNORE` in the table's own DDL, so an end-user create or update
never deletes another user's row (a `REPLACE` would remove it with no `delete` check) and never silently skips.
End-user writes never use `UPSERT` or `OR REPLACE`.

`new.*` is always the post-image the engine stored, after column affinity, defaults and triggers, never the request
body, so a value that changes on storage cannot slip past `create`, `update` or `validate`. Triggers and foreign-key
actions (`ON DELETE` or `ON UPDATE` `CASCADE`, `SET NULL`, `SET DEFAULT`) run with the database owner's authority,
and their effects roll back with a denied write. Rules gate the end user's row operation, not what the owner's
triggers and cascades then do to other rows.

### 5.3 Regions

A region that holds a replica forwards an end-user write to the home through home forwarding
(`src/TerraBase/Features/Regions/HomeForwarding.cs`) and returns the home's answer; end-user writes never take the
local write path of [write routing](sql/write-routing.md), because rules must see the authoritative row. When the
home cannot be reached, or the region holds no replica, it answers `421 wrong_region` naming the home with nothing
applied. When the home may have received the request but did not answer, the write answers `503
write_outcome_unknown`, since the home may have committed it. TerraBase never replays a write itself.

Chrome and Edge resend a buffered body once after a `421` (`sdk/js/README.md:248`). A `421` means nothing was applied,
so the resend cannot apply a write twice, but the JS client cannot stop it either; the browser client (W3) must close
or state that gap in its README. After an uncertain result (a timeout), clients must not replay: a create whose key comes from
`set.create` with `request.ulid` cannot be matched afterwards, so apps that retry creates should let the client choose
a ULID in a writable, UNIQUE `id` column and treat `409 row_exists` as done; its `details.key` is the row's key when
the caller may read it.

## 6. Entry points

| Entry point | End-user token | Project credential |
|---|---|---|
| Shape routes, row read | `read` + `columns` | Every row and column |
| Row writes and batch | Section 5.2 | Every row and column |
| SQL over HTTP, WebSocket and the PostgreSQL protocol | `403 forbidden` | As today |
| Rules routes, simulator, every management route | `403 forbidden` | `databases:read` / `databases:write` |
| File buckets | Bucket policy, below | As today |

[File buckets](file-buckets.md) under the new identity:

- A policy's `roles` match the trusted `claims.roles` list (section 3.3) in place of the deleted top-level `roles`
  claim (`FileStorageService.cs:480`).
- `public` means every signed-in end user who is not a guest. Anyone can become a guest, so a guest
  (`auth.is_anonymous = 1`) is admitted by none of `public`, `owner` or `roles` unless that read or write policy also
  says `"guests": true`.
- A token restricted to a database (a custom token with `database`) gets no bucket access, as today
  (`FileStorageService.cs:45`).

The end-user endpoint allowlist (`ProjectCheck.cs:82-92`) gains the row routes and nothing else; `SocketAdmission.cs`
keeps refusing end-user SQL sockets.

## 7. Rules routes, storage and versions

| Route | Scope | Answer |
|---|---|---|
| `GET /v1/projects/{project}/databases/{database}/rules` | `databases:read` | `200 {"version","tables","problems","updatedAt","updatedBy"}`, `ETag: "<version>"`; `404 no_rules` |
| `PUT …/rules` | `databases:write` | `200` the new version with its `warnings`; `400 invalid_rules`; `412 version_mismatch`; `428 precondition_required` |
| `GET …/rules/versions?after=&limit=` | `databases:read` | `200 {"items":[{"version","updatedAt","updatedBy","digest"}],"next"}`, newest first |
| `GET …/rules/versions/{version}` | `databases:read` | `200` that version's rules; `404 unknown_version` |
| `POST …/rules/versions/{version}/restore` | `databases:write` | `200` a new version with that body; same errors as `PUT` |
| `POST /v1/projects/{project}/databases/{database}/rules:test` | `databases:read` | Section 8 |

- `PUT` and `restore` need `If-Match: "<current version>"`, or `If-None-Match: *` when the database has no rules. A
  missing precondition answers `428`, a stale one `412` with the current `ETag`. Each success creates a version ULID;
  the last 100 versions are kept. To deny everything, deploy `{"tables":{}}`.
- `restore` checks the old body against today's schema like any deploy.
- The current rules and their versions are stored in the database's internal shape metadata
  (`_tb_shape_chunk`, keys `~rules:current` and `~rules:v:<version>`, beside today's `~grant:<table>`), replicated with
  its pages, captured in CDC so backup replay preserves them, copied by full snapshots, and deleted with the database.
- Writes go to the home region; other regions answer `421 wrong_region`. Reads of the rules answer from any replica.
- The dashboard reaches these routes through the shared MagicOnion contracts, like its other TerraBase calls.

## 8. Simulator

`POST …/rules:test` evaluates one request against the deployed rules, or against a draft sent in `rules`, without
reading or writing rows:

```json
{"rules":{"tables":{…}},"operation":"update","table":"todos",
 "auth":{"uid":"01J…","claims":{"roles":["editor"]},"org_id":null,"org_role":null,"is_anonymous":false},
 "row":{"owner_id":"01J…","title":"a"},"new":{"title":"b"},"time":1763337600000}
```

```json
{"allowed":false,"decidedBy":"update","clause":"row.owner_id = auth.uid",
 "results":[{"rule":"update","value":false}],"columns":["id","owner_id","title","done"],"problems":[]}
```

- For `update`, `new` is the patch; the simulator merges it over `row` and applies `set.update` as a write would.
  For `create`, `new` is the body and `set.create` is applied. `read` also reports the readable columns.
- It runs in any region, compiles with the same code the enforcement uses and evaluates the compiled conditions on
  the database's engine with bound parameters only. It cannot reproduce affinity, defaults or triggers on stored
  rows; the answer says what the rules decide for exactly the values sent.
- A draft gets the deploy checks; problems answer `200` with `problems` filled and `allowed: false`.

## 9. Tooling and SDKs

- **Dashboard:** a Rules page per database with an editor, a playground (the simulator), version history with diffs
  and restore, and the `problems` list.
- **MCP:** `get_database_rules`, `deploy_database_rules`, `test_database_rules`, `list_database_rules_versions`,
  `restore_database_rules_version`.
- **CLI:** `ts rules pull <database>` writes `terrascale.rules.json` (the rules JSON); `ts rules deploy <database>` reads
  the current version, shows the diff and puts with `If-Match`; `ts rules test <database> --operation … --table …
  --auth … --row … --new …` calls the simulator and exits non-zero on a deny.
- **Terraform:** `terrascale_database_rules` (`project`, `database`, `rules` as JSON; computed `version`); updates use
  `If-Match` from state, and drift is a different current version.
- **SDKs:** .NET, Go, Python and Rust manage rules (`get`, `deploy`, `versions`, `restore`, `test`) and mint custom
  tokens. The JS browser client signs in to the tenant (PKCE, guests, magic link), refreshes tokens, reads rows and
  live shapes, and creates, updates, deletes and batches rows.

## 10. Naming

The data feature is **Rules**; "grant" left it. The table read grant routes, their endpoints, storage and error codes
are deleted, and the first rules deploy of a database deletes its old `~grant:` rows in the same home write
transaction. `ProjectAccessResolver.cs` resolves project access, not data rules. Dashboard collaborator grants and
ts-auth OAuth grants keep their names.

## 11. Errors

| Status | Code | When |
|---|---|---|
| `401` | (Bearer challenge) | Missing, expired, tampered or wrong-audience token |
| `403` | `rule_denied` | The operation's rule is missing or false; `details.operation`, `details.table` |
| `403` | `column_not_readable` | A request `where` or `columns` names a column outside `columns`; `details.column` |
| `403` | `column_not_writable` | A body names a column outside `writable`, or a `set` column; `details.column` |
| `403` | `rules_invalid` | The rule needed no longer compiles against the table |
| `403` | `forbidden` | An end-user token on SQL or a management route |
| `400` | `validation_failed` | A `validate` clause is false; `details.clause` is its index |
| `400` | `invalid_rules` | A deploy, restore or simulator draft failed its checks; `details.problems` |
| `400` | `unknown_column`, `constraint_failed`, `invalid_request` | Bad body, engine constraint, malformed request |
| `404` | `row_not_found` | The row is missing or not readable |
| `404` | `unknown_database`, `unknown_table`, `no_rules`, `unknown_version` | As named |
| `409` | `row_exists` | A create or update hit a UNIQUE or primary key; `details.key` when readable |
| `409` | `must_refetch` | The shape handle changed (rules deploy, new binding) |
| `412` | `version_mismatch` | Stale `If-Match` |
| `421` | `wrong_region` | Home unreachable or no replica; nothing applied |
| `428` | `precondition_required` | `PUT` or restore without `If-Match` / `If-None-Match` |

A denial says which operation and table, never the condition text; the simulator is for the owner.
`409 row_exists` tells a caller allowed to create that a key is taken even when it cannot read that row. Use ULID keys.

## 12. Later

Named, not designed here:

- **Lookups in conditions:** `exists(…)` and `get(…)` into other tables (Firestore `get()`/`exists()`), for
  membership tables such as "a member of this row's team". They must read in the same transaction as the write and be
  part of the handle's binding on reads.
- **JS offline persistence:** a local store with queued writes that replay through the row API.
- **A per-user request budget**, beside the per-database admission.

## 13. Rollout

Each implementation category adds its own journey steps (two users, cross-user denial, hidden columns, writes, a rules
deploy that revokes) as `e2e/AGENTS.md` describes.

**W0 fix/terrabase-shapes.** In today's grants: validate the request `where` and `columns` against the grant's
columns only, answering `403 column_not_granted`, so a hidden column cannot be inferred by filtering
(`ShapeFilter.cs:69`); bind `:sub` as a parameter instead of splicing it (`ShapeFilters.Bind`) and add the grant's
`updatedAt` and the bound subject to the handle fields (`ShapeHandle.cs:23`). Journey: a second user filtering on a
hidden column is refused; two users never share a handle.

**W1 feat/terrabase-rules.** The rules set, its storage under `~rules:` keys, versions, the section 7 routes
with `If-Match`, deploy checks and `problems`; the validator extended with qualified and typed references, parameter
binding, `IS TRUE` compilation and list membership; read enforcement on shapes and the row read with the section 4
handle, cache and column rules. Deletes the table read grants (section 10), stops reading the `tables` claim, and
renames `ProjectGrants.cs`.

**W1 feat/auth-data-tokens.** ts-auth: the data audience on every tenant access token; `custom_claims` on users,
copied into tokens as `claims`; the `auth:tokens:mint` scope; `POST /v1/{project}/custom-tokens` signed by the tenant
ring with `token_use: custom`, 5 minutes, `database` only; machine clients refused a data audience. Deletes the platform `end-user-tokens` route, its
`tables` and `roles` fields and its OpenAPI file; updates `docs/auth/scoped-end-user-tokens.md`. Lands after
feat/terrabase-identity, and in the same PR removes TerraBase's platform `token_kind: end_user` branch
(`UserAuthenticationHandler.cs`), which nothing mints any more.

**W1 feat/terrabase-identity.** TerraBase: per-project tenant issuer and JWKS selection from the route project
(section 3.1), validation, and the mapping of tenant and custom tokens to `ProjectAccess` and the `auth.*` values;
guests allowed; machine tokens refused; hints sockets closed at `exp`. File buckets move in the same PR: `roles`
from `claims.roles`, `public` without guests, and the `guests` policy field (section 6).

**W2 feat/terrabase-rows.** The section 5 row API: create, update, delete and batch in the home write transaction
with the pre-read and post-image checks, `set` and `writable`, forwarding and `421`, error codes, OpenAPI,
the end-user endpoint allowlist, CORS methods, allowance metering.

**W2 feat/terrabase-rules-test.** The section 8 simulator on the same compiler, with draft checks and the deciding
clause.

**W3 feat/sdk-js-client.** The browser client: tenant sign-in (PKCE, guests, magic link) and refresh, row reads,
live shapes that refetch on `409`, row writes and batches; the `421` resend gap closed or documented.

**W3 feat/sdk-rules.** Rules management (`get`, `deploy`, `versions`, `restore`, `test`) and custom-token minting in
.NET, Go, Python and Rust, with the journey's SDK operations.

**W3 feat/cli-rules.** `ts rules pull|deploy|test` and `terrascale.rules.json`.

**W3 feat/terraform-rules.** `terrascale_database_rules` in terraform-provider-terrascale.

**W3 feat/dashboard-rules.** The Rules page (editor, playground, history, restore, problems) and the five MCP tools.

**W3 docs/rules-quickstart.** A docs-site concept page for Rules and a "todo app with no backend" quickstart: sign
in as a guest, deploy the section 1 rules, read live and write from the browser.
