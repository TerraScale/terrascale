# Management commits (`/v1/management/commits`)

TS-521. ts-dashboard keeps its atomic `core.Store` contract (ts-dashboard `docs/store-contract.md`) on
top of TerraBase. One logical Store update, its Management changes and the dashboard-owned documents
that go with them, is **one commit**: every change becomes visible together or none does.

Why it is one SQLite transaction: the Management catalog lives in the system database (`_system.db`),
and SQLite in WAL mode is atomic only within one file. The dashboard-owned documents that take part in
commits therefore live in the system database too, in the **dashboard document space** described below,
and not in a tenant database. See [decision 0008](decisions/0008-management-commits.md).

All routes here use the Management API access rules ([management-api.md](management-api.md#access-and-authorization),
[decision 0007](decisions/0007-ts-auth-user-tokens.md)): a ts-auth access token on the client listener, whose `sub`
is the acting user, or the management service credential with `X-TerraBase-User-Id` on the loopback administrative
listener. Each operation names its organization.

**The dashboard document space needs the management service credential.** A ts-auth user token reaches
Management operations as that user, but never dashboard documents: `GET`/`query` on them and any commit with a
document operation answer `403 forbidden`. Dashboard documents hold every user's state, so a signed-in user's
own token must not read or write them. How a remote ts-dashboard authenticates as the trusted service for
this space, while acting as the signed-in user for Management operations, is an open decision (see
[decision 0008](decisions/0008-management-commits.md#open-question)); until it is made, only the loopback
credential reaches the space.

## Commit

`POST /v1/management/commits` with `Content-Type: application/json`, at most 4 MiB:

```json
{"id":"0199…","ops":[{"op":"fence","organization":"0199…","version":"812"},
                      {"op":"invite","organization":"0199…","email":"ada@example.com","role":"viewer","token_hash":"9f86…"},
                      {"op":"patch","collection":"organizations","id":"5f1c…","merge":{"invites_sent":3}}]}
```

- `id` is a lowercase hyphenated UUID the client chooses once per **logical** update and reuses on every
  retry of it (see [Uncertain outcomes](#uncertain-outcomes)).
- `ops` holds 1 to 1024 operations. An operation with a `collection` member is a
  [document operation](#document-operations); any other is a [Management operation](#management-operations).
  Unknown, repeated or `null` members are rejected.
- At most 16 database lifecycle operations (`create_database`, `branch_database`, `delete_database`),
  16 `assert_count` and 16 `limit` operations per commit. Two document operations may not target the same document.

### Evaluation

1. `fence` operations and document `assert`/`assert_count` operations, in order, against the state before
   the commit. `limit` operations record their count.
2. Every other operation, in order. Each sees the effects of the operations before it, so an operation
   may name an organization or project created earlier in the same commit (by its client-chosen `id`).
3. `limit` operations compare the count after the commit with their bound.

The first failure aborts the commit: nothing is written and no commit record is stored. The error names
the failing operation (`error.op`). Authorization, membership, roles, name uniqueness, the last-owner rule
and every precondition are checked inside the commit's write transaction, the same one that writes, so no
concurrent change can land between a check and the write. Commits are serialized by the system database's
single writer.

### Response

`200`:

```json
{"id":"0199…","offset":"9031",
 "results":[{"asserted":true},{"value":{"id":"0199…","email":"ada@example.com","role":"viewer","status":"pending",…}},{"id":"5f1c…","version":"…"}],
 "versions":{"account":"9031","organizations":{"0199…":"9031"}}}
```

- `offset` is the system database commit offset.
- `results` has one entry per operation: `{id, version}` for document writes, `{asserted:true}` for fences
  and assertions (plus `count`, a decimal string: the matches of `assert_count`, the count after the commit
  of `limit`), and `{value}` for Management operations that return a resource (`{}` for those that do not).
  `delete_database` takes the database id, as its REST route does.
- `versions` holds the acting user's account version and the version, after the commit, of every
  organization an operation named that still exists and still has the user as a member.

### Errors

`{"error":{"code","message","op"}}`, never echoing input:

| Status | Code | When |
| --- | --- | --- |
| 400 | `invalid_request` | malformed body or operation |
| 403 | `forbidden` | the acting user's role does not allow the operation |
| 404 | `not_found` | unknown resource, or an organization the user is not a member of (as on every Management route) |
| 409 | `name_taken`, `id_taken`, `not_empty`, `last_owner`, `already_member`, `already_owner`, `invitation_pending`, `invitation_closed`, `token_taken`, `expired` | the rule in [management-api.md](management-api.md) or below |
| 409 | `limit_exceeded` | a `limit` operation failed |
| 409 | `commit_id_reused` | the commit id was already committed with a different body |
| 412 | `precondition_failed` | a stale `fence`, a failed document `if_match`/`if_none_match`, `assert` or `assert_count` |
| 413 | `payload_too_large` | body over 4 MiB |

## Versions and fencing

Every organization has a **version**, and so does every user's account. Versions are opaque strings
(decimal commit offsets today); compare them only for equality. They are stored in the catalog and stay
the same across restarts until the next change.

- An organization's version changes with **every** Management change to it or anything under it, made by a
  commit or by any other `/v1/management` route: rename, plan, projects, databases, members, roles,
  invitations, ownership transfer, audit retention, and the project state of the database tooling (saved queries,
  project API keys, backup policies). Data-plane tooling (indexes, backups themselves, SQL) does not: the Store keeps
  none of it in its aggregate. A version that has never changed reads `"0"`.
- An account's version changes when the user joins or leaves an organization (any path), when a role of
  the user changes, and when the current organization changes.
- `GET /v1/management/versions?organization=<id>` (0 to 16 `organization` parameters) answers
  `{"account":"…","organizations":{"<id>":"…"}}` in one snapshot; `404` if the user is not a member of one
  of them.

`{"op":"fence","organization":"<id>","version":"<v>"}` (or `{"op":"fence","account":true,"version":"<v>"}`
for the acting user's account) fails with `412` unless the version before the commit equals `v`, and then
**advances** the version. Several fences on the same version in one commit all compare with the version before it. A fence is therefore a compare-and-set, not only a read check: two commits that fenced the same
version cannot both commit, even when they write disjoint documents. That is the aggregate lock the Store
contract needs; dashboard root CAS alone is not enough, because Management routes outside a commit change
the version too. An organization fence needs membership (any role).

The dashboard's aggregate repository loads the version **first**, then the aggregate (Management reads and
dashboard documents), applies the pure callback, and commits the diff with a fence on the version it loaded.
A `412` means another writer committed first: reload and rerun the callback. Reads after the version that
straddle a concurrent change are caught by the fence, so the loaded aggregate never needs a single snapshot read.

What the aggregate holds depends on the principal, as the REST routes do: invitations, audit events and audit
retention need `admin`, so the adapter loads them only for admins, and a non-admin's callback cannot change
them (any such operation is `403`).

**Updates without a principal.** Every Management operation acts as a user, so trusted integrations that act
without one (the Store's `UpdateOrganization` Stripe sync) cannot fence an organization, and need not: they
change only dashboard documents (billing, checkout sessions), and commit them with document `if_match` and
`assert`. Because commits are serialized and assertions run inside the commit, a user's commit that asserts
the billing document's plan and a sync that rewrites it never both commit against the same plan. Platform-admin
changes to other users' organizations (`UpdateAccountAsAdmin`) need an admin authorization design and are not
served, as for the REST routes.

## Limits

`{"op":"limit","organization":"<id>","databases":N}`, `…"members":N}` or `…"active_members":N}` (exactly one
bound, `N` from 0 to 1000000):

- `databases` counts the organization's databases over all its projects.
- `members` counts members plus pending, unexpired invitations: the Store's rule for `invite`.
- `active_members` counts members only: the Store's rule for `accept_invitation`, where accepting turns a
  pending invitation into a member and so would not grow `members`.

The commit fails with `409 limit_exceeded` when the count after the commit is above `N` **and** above the
count before it. A commit that does not add (deletes, renames, unrelated writes) never fails a limit, so an
organization already past its limit after a downgrade keeps what it has but cannot add more. That is the
Store's `withinDatabaseLimitLocked` and member-limit rule.

A limit needs the user to be a member (any role) before or after the commit, so it also guards a commit that
starts membership (`accept_invitation`) or ends it (`leave`); otherwise it is `404 not_found`, as for any
organization the user is not a member of. An organization created in the same commit counts as empty before it.

TerraBase does not know dashboard plans. The dashboard binds `N` to its plan in the same commit with a
document assertion on its plan document, for example
`{"op":"assert","collection":"plans","id":"…","filter":{"eq":["/max_members",5]}}`, and on the organization's
billing document's plan id. A concurrent plan change then fails one of the two commits.

## Management operations

Members are snake_case. Names, emails and roles follow the REST rules in [management-api.md](management-api.md).
`organization` and `project` are ids; `id` on a create is an optional lowercase hyphenated UUID chosen by the
client (otherwise a UUIDv7 is generated), which lets later operations and dashboard documents in the same
commit refer to the new resource. A client id already used by any organization (or any project, or any database, deleted
ones included) is `409 id_taken`, even one of another tenant: ids are random UUIDs, so this reveals only that a UUID the
client already holds is in use. A database id is chosen to register, in another region, a database that region replicates
under the same ids ([cross-region replication](cross-region.md#registering-a-database-in-both-regions)).

| `op` | Members | Needs | `value` |
| --- | --- | --- | --- |
| `create_organization` | `name`, `id`? | | organization; the user becomes its owner |
| `rename_organization` | `organization`, `name` | admin | organization |
| `set_plan` | `organization`, `plan` | owner | organization |
| `delete_organization` | `organization` | owner | none |
| `set_current_organization` | `organization` | viewer | organization |
| `create_project` | `organization`, `name`, `id`? | member | project |
| `rename_project` | `organization`, `project`, `name` | member | project |
| `delete_project` | `organization`, `project` | member | none |
| `create_database` | `organization`, `project`, `name`, `id`? | member | database (with `region`) |
| `branch_database` | `organization`, `project`, `database`, `name` | member | database (with `region`) |
| `delete_database` | `organization`, `project`, `database` | member | none |
| `change_role` | `organization`, `user`, `role` | admin; owner for owners | member |
| `remove_member` | `organization`, `user` | admin; owner for owners | none |
| `leave` | `organization` | viewer | none |
| `invite` | `organization`, `email`, `role`?, `token_hash` | admin; owner to invite an owner | invitation |
| `resend_invitation` | `organization`, `invitation`, `token_hash` | admin; owner for an owner invitation | invitation |
| `revoke_invitation` | `organization`, `invitation` | admin | none |
| `accept_invitation` | `token_hash` | the invitee (verified email) | `{organizationId, member}` |
| `decline_invitation` | `token_hash` | the invitee (verified email) | invitation |
| `start_transfer` | `organization`, `user` | owner | transfer |
| `confirm_transfer` | `organization` | the nominee | member |
| `cancel_transfer` | `organization` | owner or the nominee | none |
| `set_audit_retention` | `organization`, `days` | admin | `{days}` |
| `fence` | `organization` or `account: true`, `version` | viewer | none |
| `limit` | `organization`, `databases` or `members` | viewer | none (`count`) |

"Needs member" means the `member` role or higher, and "needs viewer" any membership: a **viewer** is
read-only and gets `403` for every project and database change. Roles in commits and on the REST routes are
`owner`, `admin`, `member` and `viewer`.

### Invitations by token hash

The dashboard generates the invitation token, keeps it only in the link it delivers, and sends
`token_hash`: the lowercase hex SHA-256 of the token (64 characters). TerraBase never receives or stores the
plaintext token of these invitations, and a commit record therefore holds no secret.

- `invite` creates a `pending` invitation. A second pending, unexpired invitation for the same email in the
  organization is `409 invitation_pending`. A hash already used by any invitation is `409 token_taken`.
- `resend_invitation` gives a pending invitation (expired or not) a new hash, creation time and 7-day expiry;
  the old hash stops working and stays taken: no invitation can use it again (`409 token_taken`).
- `accept_invitation` and `decline_invitation` find the invitation by hash. The email is the access token's
  `email` claim with `email_verified` `true`, as for `POST /invitations/accept`, never a request member: no verified
  email is `403`. It must match the
  invitation ignoring case (`403`); an unknown hash is `404`; an invitation that is no longer pending is
  `409 invitation_closed`; an expired one is `409 expired`. Accepting makes the user a member with the
  invited role, a declined invitation becomes `declined`. Each succeeds at most once: concurrent redemptions
  serialize and every one after the first is `409 invitation_closed`.
- `POST /v1/management/invitations/lookup` with `{"tokenHash":"…"}` answers a pending, unexpired invitation
  as `{"organization":{"id","name"},"invitation":{…}}` and `404` otherwise. It reveals only the organization's
  id and name, like the Store's `FindInvitation`.

Invitations keep their history: `GET …/invitations` lists every invitation of the organization in creation
order with its `status` (`pending`, `accepted`, `declined` or `revoked`; a `pending` one past `expiresAt` is
expired). Revoking marks it `revoked`. Inviting an email whose pending invitation expired creates a new
invitation and leaves the expired one in the history.

## Document operations

Exactly the operations of `POST /v1/projects/{project}/databases/{database}/transactions`
(Documents API v1): `create`, `put`, `patch`, `delete` (with `if_match`/`if_none_match`), `assert` and
`assert_count`, with the same members, rules and limits. Versions are strong ETags as in the Documents API.

They address the **dashboard document space**, not a tenant database: one set of collections inside the
system database, reachable only with the management service credential (never with an API key or a ts-auth user
token). Collections follow the
data API naming rule and exist implicitly; there is nothing to create. The dashboard is trusted with this
space: it decides which documents a user may touch, while Management operations are still authorized per user.

- `GET /v1/management/documents/{collection}/{id}` reads one document (`ETag` is its version; `404 not_found`).
- `POST /v1/management/documents/{collection}/query` runs a query v1 body (`filter`, `sort`, `project`,
  `limit`, `cursor`, `count`) over the space.

## Uncertain outcomes

A commit whose reply is lost, or that was running when the server restarted, has one of two outcomes:
committed or not. The commit id makes it observable:

- The commit record (id, acting user, SHA-256 of the request body, the response) is written **in the same
  transaction** as the commit. It exists exactly when the commit's changes do, including after a crash.
- `GET /v1/management/commits/{id}` answers the recorded response for a committed commit of the acting user,
  and `404` otherwise: **not committed (yet)**, failed (failures record nothing), more than 7 days old, or another
  user's. A `404` is not final: the original request may still be running. Resolve it by resending the same id
  and body, never by retrying the logical update under a new id.
- Sending the same id again with a **byte-identical** body answers the recorded response, byte for byte,
  with `TerraBase-Commit-Replayed: true` (a first reply has no such header), without applying anything. A different body under a committed id is
  `409 commit_id_reused`. Concurrent duplicates serialize; only one applies.
- A retry of an id that never committed is evaluated like a new request, with the same fences and assertions,
  so it cannot double-apply an update that a concurrent writer made stale.

So the SDK may resend a commit with the same id and body after a timeout, a connection error or a 5xx, and
must not invent a new id for it. A new id means a new logical update. The Documents API still rejects
`Idempotency-Key`; it is not a commit id.

Records are kept for 7 days; each commit deletes up to 100 expired ones.

## Databases in commits

Creating, branching and deleting databases touches files, which cannot join a SQLite transaction. The
commit keeps the guarantees of the database lifecycle recovery in [management-api.md](management-api.md#database-lifecycle-recovery-ts-480):

- **Create and branch:** before the commit's transaction, TerraBase checks the acting user may create in the
  project (or may become a writer through an earlier `accept_invitation` or `confirm_transfer`, or creates the
  organization or project earlier in the same commit), records a cleanup intent and creates the files under it.
  A branch copies its source then; inside the transaction its source must still exist where the operation runs
  (`404` otherwise, for example after an earlier `delete_database` of it), and an organization or project created
  in the same commit has no database to branch. The commit registers the database and clears
  the intent in its transaction. A failed commit removes the files and the intent. A crash before the commit
  leaves the intent, which the next start replays, removing the files. The database is visible (listed,
  reachable through the data API) only once the commit is visible.
- **Delete:** the commit removes the registration and records the cleanup intent in its transaction; the
  files are removed after it commits. A crash after the commit leaves the intent for the next start. The
  database is gone from the catalog and the data API as soon as the commit is visible.
- A failure while removing files after a committed delete answers `500`; the commit record still exists, so
  `GET /v1/management/commits/{id}` reports it as committed, and the next start finishes the cleanup.

## Store parity

| Store behavior (ts-dashboard `docs/store-contract.md`) | TerraBase |
| --- | --- |
| Aggregate update, serializable over the aggregate | one commit with a `fence` on the organization (and the account) |
| Mutate callback may rerun | `412` from a fence or document precondition; reload and rerun |
| `ErrConflict` on unique names | `409 name_taken` |
| Database limit, downgrade keeps existing (invariant 7) | `limit` with `databases`, plan bound by a document `assert` |
| Member limit, pending invitations count (invariant 11) | `limit` with `members` |
| Invitations by token hash, single use, history, resend (invariant 12) | [Invitations by token hash](#invitations-by-token-hash) |
| Viewer is read-only | native `viewer` role, never mapped to a writable one |
| Exactly one owner, transfer swaps owner and nominee | the dashboard grants `owner` only through `start_transfer`/`confirm_transfer`; TerraBase refuses to leave an organization without an owner |
| Billing plans (`plan_free`, …) | dashboard documents; the native `plan` (`hobby`, `pro`, `enterprise`) is unrelated quality-of-service metadata and is not derived from them. Billing behavior does not change |
| `commit_uncertain` retry with the same key | same commit id and body, or `GET /v1/management/commits/{id}` |

## Crash and restart

Commits use `synchronous = NORMAL` like every TerraBase write (decision 0003): a committed commit survives a
TerraBase process crash; an operating-system crash or power loss may lose the latest commits, together with
their commit records, never one without the other.
