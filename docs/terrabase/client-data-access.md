# Client data access (v1)

End-user access tokens authenticate a user in one ts-auth project. Project tokens and API keys retain their existing scopes and project/database boundaries. SQL, database/collection management, schema, TTL, inferred metadata and access-rule administration require project credentials. Public collection rules grant access to authenticated end users; anonymous requests and anonymous tokens remain denied.

## Rules API

A project credential with `documents:write` sets a policy using:

```http
PUT /v1/projects/{project}/databases/{database}/collections/{collection}/rules
Content-Type: application/json

{
  "owner": "/userId",
  "read": { "owner": true, "public": false, "roles": ["support"] },
  "write": { "owner": true, "public": false, "roles": [] }
}
```

`GET` at the same route requires `documents:read`. Both operations return `{ "rules": <policy> }`. A collection without rules returns `{ "rules": null }`; it denies every end-user operation. To revoke every grant, PUT policies with `owner:false`, `public:false`, and empty roles arrays. There is no executable rule language.

The body is at most 16 KiB. Both `read` and `write`, and each policy's `roles` array, are required. Omitted grant booleans default to false. Unknown members, duplicate JSON members, null roles, duplicate roles and malformed pointers fail with `400 invalid_rules`. Each roles array has at most 32 distinct strings, each 1–128 characters without controls. Role comparison is ordinal and case-sensitive. The trusted roles come only from the signed ts-auth token.

`owner` is null or a JSON Pointer with 1–16 object-member segments and at most 4 KiB. Owner grants require a pointer. Arrays and numeric coercion are unsupported. The owner value must be a nonempty string of at most 256 characters equal to the signed subject. `/id` and every path rooted at `/id` are rejected because document identity is virtual and removed from stored payloads.

The three grants are alternatives: `public` permits every authenticated end user; any matching trusted role permits collection-wide access; `owner` permits the subject's documents. Read and write grants are independent. A PATCH authorized only for writing omits the stored document from its response, including transaction results, when the result is not readable by that identity. Project credentials bypass collection rules while retaining their existing scope boundaries.

## Documents, queries and transactions

Rules cover generated-id create, by-key get/put/patch/delete, list, query, and every transaction operation. Ownership is checked within the write transaction before preconditions can reveal another user's state. An end user cannot assign a newly created document to another subject, transfer its current owner, or recreate another owner's deleted id. Explicit role/public write grants permit editing other documents while retaining their owner. Server project credentials can transfer ownership.

Owner predicates enter storage queries before filters, ordering, vector ranking, limits, counts and cursor generation. Transaction `assert_count` counts only readable rows; point assertions cannot probe another user's document. A rejected operation rolls back the whole transaction. Writes carry the original request identity across the group-commit worker; another request's identity is never substituted.

Point and query caches are bypassed for end users, so a project-token cache fill cannot disclose its unfiltered contents. List/query cursors bind the database, collection, query, signed subject, sorted role set and current rule fingerprint. A cursor used by another identity, after a role change, or after a rule change fails with `400 invalid_cursor`.

An existing collection without a read grant returns `403 access_denied`. A document hidden by an otherwise usable owner policy returns `404 not_found`, without an ETag, body, conflict detail or tombstone version. A denied write/assertion returns `403 access_denied`. Normal validation, precondition, schema, capacity and tenant-isolation errors retain their existing meanings. End users receive `403` when attempting rule administration.

## Changes and shapes

End-user SSE change pages use a request-specific storage snapshot rather than the shared project-token page cache. Current rules authorize every emitted afterimage. A delete or ownership-exit event can name an id only when an exact retained owner proof matches the collection generation, JSON Pointer, signed subject and complete rule fingerprint. Public/role read grants authorize collection-wide events. Internal collection/index metadata never appears as data events.

A change cursor binds the database/selected collection set, signed subject, sorted roles, collection generations and all rules in that set. Cross-user, changed-role and changed-policy cursors return `410 cursor_expired`. The unfiltered feed covers every collection: adding/dropping a collection also changes that scope. A running stream re-evaluates its policy before each page and after a commit wake; a scope change ends it with `error` / `rules_changed`. Discard local state and take a fresh authorized snapshot before resuming. No-rule feeds return `403` before opening the stream.

Origin shapes support owner, public and role policies. Their effective filters and handles bind current rules and ownership. Owner predicates run in SQL before snapshot pagination, so continuation keys never disclose unrelated ids. Chunk deletes require exact retained owner proof independent of the caller's `where` filter; matching the user filter alone grants nothing. Current upserts are independently owner-checked. Policies are checked before and after reads, including each waiter returning from a shared live flight. A changed policy requires a fresh authorized bootstrap; stale handles cannot read prior-policy chunks. End-user mirror manifests and epoch keys return `403` before handle resolution; origin synchronization remains available. Project credentials retain mirror access.

Previously delivered data cannot be recalled. Rule changes on one replica become visible on another through normal collection-metadata replication; authorization uses the committed policy of the serving replica. Token expiry still bounds an open stream. If a deletion arrived before any authorized preimage was known on that replica, or its proof belongs to a different policy/generation, ownership cannot be proven and that id is omitted. A fresh authorized snapshot is the recovery path; no unrelated tombstone id is guessed or disclosed.

## Persistence and rollout

Rules are members of the replicated `_collections` JSON document and preserve unrelated schema/TTL/table metadata. Collection drop/recreate starts a new generation without prior rules. The local `_tb_feed` additionally persists two nullable TEXT columns, `access_before` and `access_after`, containing bounded `{generation,path,subject,policy}` proofs captured from the prior/resulting lattice state. Proofs contain owner identity only, not document preimages. They are derived locally for local and joined writes, retained with the feed, excluded from public events, and included in read-page memory/byte admission.

**Needs format bump.** The SQLite feed schema changes. This feature does not bump `StorageFormat`, migrate existing files, reset data, or deploy. The deploy/reset owner must batch the format bump and obtain the existing reset approval before advancing existing databases. Old database files cannot be opened with the new feed-column assumptions until that coordinated rollout. No serving infrastructure or production data is deleted by this implementation.
