# 0004: Document API v1 on SQLite

- Status: proposed (needs human confirmation of the divergences below)
- Scope: `src/TerraBase/Features/Documents`, `src/TerraBase/Engine`
- Contracts: `terrabase-rust/docs/api-v1.md`, `query-v1.md`, `control-plane-v1.md`

## Decision

The client listener serves the Documents API v1 routes for one SQLite file per database:
point reads and writes (`GET`/`PUT`/`POST`/`PATCH`/`DELETE …/documents`), listing, queries
(`POST …/collections/{collection}/query`) and transactions (`POST /v1/projects/{project}/databases/{db}/transactions`),
with `{"error":{"code",…}}` bodies.

TS-754 carries caller-supplied keys in one UTF-8 query parameter for point operations:
`GET/PUT/PATCH/DELETE …/documents/by-key?id=<encoded key>`. Keys are decoded exactly once, with no path
or Unicode normalization. This replaces the path-key routes because Kestrel normalizes dot segments
and aliases escaped slash spellings before route values are exposed. See [the ID contract](../id-contract.md).

- **Fresh schema v11** (`PRAGMA user_version = 11`): new files bootstrap the complete schema in one transaction.
  Older nonempty files and newer schemas are refused. There are no upgrades, backfills or legacy ID fallbacks.
  Collections have a persisted canonical ULID `entity_id`; database incarnations are canonical ULIDs.
  An incarnation persists across reopen; newly created and copied databases receive fresh incarnations.
  `IDatabaseStore.Incarnation` exposes this value to database-scoped handle consumers and replication.
  The ID cutover uses data-directory layout 2 and catalog version 6; deployment discards affected old data.
  See [the ID contract](../id-contract.md).
- **Versions** are `tbv1-` and the first 16 bytes of SHA-256 over the database scope, collection, id,
  merge version, deletion state and raw UTF-8 document bytes. The scope of a client database derives from its
  organization, project and database ids, so every replica answers the same version for the same state (TS-574);
  recreating or copying a database creates new ids. `_system` keeps its incarnation, which catalog followers adopt.
  Including the state prevents equal-version replication winners from reusing a strong validator. A single writer
  per database never has siblings, so every document is `state:"value"`. The version hashes local metadata rather than the contract’s
  canonical causal-context encoding. This prevents token reuse when a database is deleted and
  recreated at the same name/offset. The closed-beta reset invalidates existing version tokens and credentials. Clients recreate their state and bindings.
- **Queries** compile to one SQLite statement (`json_type`, `json_extract`, `json_each`), with
  keyset cursors bound to a digest of database, collection, filter, sort and projection. Cursor
  rank/value pairs are validated before they become query parameters. Projection paths are
  normalized once by case-sensitive decoded segments: duplicates and descendants covered by a
  selected ancestor are removed before copying. Materialization checks cancellation per document
  and per effective path, and copied subtrees use case-sensitive raw JSON parsing.

## Divergences from the contracts

1. Collections are created, listed, read and dropped on the client listener
   (`/v1/projects/{project}/databases/{db}/collections…` since [0005](0005-multi-tenancy.md), `documents:read`/`documents:write` scopes). The contract
   puts collection management in the control plane (`/v1/dashboard`, control keys) and makes
   `DELETE` return `202` with an operation; here `DELETE` drops synchronously and returns `200`.
   Collections are schemaless; `schema`, `ttl_path`, `unique`, `indexes` and `advisor` fail as
   unknown members.
2. `Idempotency-Key` is rejected with `400 invalid_request` until retries are deduplicated.
3. `durability` is always `local_durable`, the contract's label for a commit to the local file.
   With `synchronous = NORMAL` (decision 0003) an acknowledged write survives a process crash but
   not a power loss or OS crash, so the label promises more than the engine does. **Needs a human
   decision**: keep the label, return FULL-synced commits for this profile, or add a profile value.
4. Number comparisons in queries use SQLite's 64-bit integer and double arithmetic, not exact
   decimals: integers outside the signed 64-bit range and decimals with more than about 15
   significant digits may compare inexactly. Patch `inc` is exact (arbitrary-precision decimal,
   result within the 128-digit document limit). Documents, patches and queries validate the decimal
   text before arithmetic: nonzero magnitudes below `1e-307` or at least `1e308` are rejected,
   including underflow and subnormal values. This is narrower than the named JSON contract's
   normalized scale range of ±1024.
5. `count` and `assert_count` examine at most 100000 and 10000 documents in id order, whatever the
   filter matches: beyond that `count.exact` is false (`value` is a lower bound) and `assert_count`
   fails with `assert_requires_index`.
6. Not implemented yet: `{"lower":…}` paths, `explain`, the 4096-document scan bound and
   field expression indexes, HMAC-signed and expiring cursors,
   TTL, unique constraints, schemas, key grants per database/collection and row filters.

## Duplicate members

`document-contract.md` delegates public JSON validation to `named-json-document-contract.md`,
which rejects duplicate decoded member names (including `"a"` and `"\u0061"`). Document bodies
therefore reject duplicates too. A transaction checks its envelope separately; duplicate members
inside an operation or its document are rejected while parsing that operation, so the error carries
its zero-based `op` index. Repeated envelope members are request-level errors without an `op`.

## Query resource limits

SQLite progress callbacks interrupt queries and counts after 5 million VM instructions or 5 seconds,
and honor request cancellation. Each execution removes its callback before the session is reused.
Budget exhaustion returns `400 invalid_query` with the fixed message `query too expensive`.
The deadline is checked at progress callbacks, so a single SQLite function or a lock wait can run
past it before the next callback; this is not a hard wall-clock latency guarantee.

Before a field sort, an index-ordered metadata scan permits at most 10000 live candidates and
8 MiB of candidate JSON. It stops as soon as either limit is exceeded and returns
`400 sort_requires_index`, even for `limit=1` or a selective filter. This conservative admission
rule bounds bodies retained by SQLite's sorter until field indexes are available. Sorts using
only `$id` use the live-document index and skip this scan. The contract's exact 4096-document
scan paging is still absent; VM/deadline budgets are an additional execution limit.

Repeated sort paths retain their first occurrence; keys after the unique `$id` are omitted
before admission, compilation and cursor processing; encoded cursors are capped at 1 MiB.
Cursor-producing string sort values are limited to 64 KiB of UTF-8 each. Even four keys with
worst-case JSON escaping and base64 expansion fit within the query request limit (4 MiB);
The complete compact query with its replacement cursor must also fit 4 MiB;
exceeding either limit returns `400 sort_requires_index` before a page is returned.
Generated-id POST uses PUT's strict conditional-header parser: malformed values fail with 400,
If-Match fails with 412 for an absent generated id, and If-None-Match: * permits creation; explicit null operation members are invalid.
A database whose commit offset reaches Int64.MaxValue refuses document writes with `503 capacity`
without changing its data or counter.
