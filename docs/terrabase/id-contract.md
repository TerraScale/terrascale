# Entity ID contract (TS-754)

Every entity ID TerraBase generates is a plain 26-character Crockford ULID, canonical uppercase, without
prefixes or UUID formatting. Organizations, users, projects, databases, collections, automatically keyed
documents, API-key records, invitations, audit events, backups, management commits and internal random
identities use this contract. Entity routes and inbound entity references reject malformed or noncanonical IDs.

Generation uses maintained ULID libraries: NUlid provides monotonic cryptographic randomness and Cysharp
Ulid provides the existing value type and canonical encoding. A single generator per application clock
serializes timestamp sampling and randomness; IDs generated within one millisecond increase lexicographically.
This ordering is per process, not a distributed sequence. Clock changes can change timestamp order.

The document key is its entity ID. Stored entity payloads omit a redundant `id`; typed management responses
recover identity from the key. A document write removes a root `id` equal to its key, leaving unrelated data
fields intact. API envelopes may return `id` beside `document` so clients can address the stored document.

Caller-supplied document keys retain their bounds-only contract: 1–256 characters, valid UTF-8, no control
characters. They need not be ULIDs. This applies equally to direct writes and transaction operations.
Names, counters, pagination sequences, secret hashes and query fingerprints are lookup values rather than
entity IDs; they are not generated entity identities.

Point reads and writes use `GET/PUT/PATCH/DELETE …/documents/by-key?id=<UTF-8 query-encoded key>`.
Dashboard document reads use `GET /v1/management/documents/{collection}/by-key?id=<encoded key>`.
The `id` parameter is required exactly once and is the only accepted query parameter. Encode the raw key
with a standard query encoder and decode it once on the server; do not predecode, normalize Unicode or
put the key in a path segment. `a/b`, `a%2Fb`, `.`, `%2E`, `..`, `%2E%2E`, `%`, `?`, `#`, plus, space and
Unicode are addressable as distinct keys. Query `+` denotes a space; a literal plus is `%2B`.
Invalid percent escapes and invalid UTF-8 are rejected. The old `…/documents/{id}` point routes are removed.
Listing and generated creation still use `GET/POST …/documents`; creation returns the `by-key` Location.

Fresh storage uses data-directory layout 1, SQL schema 1 and catalog version 1, and records its storage format (the
bootstrap schema's digest) in the lock file; a build refuses a directory of any other format
([schema compatibility](replication.md#schema-compatibility)). New files bootstrap the full schema atomically. Nonempty files of any other schema version are refused, on import as well as normal startup.
There are no migrations, backfills, dual reads/writes or legacy ID fallbacks. TS-755's native uniqueness is
part of this schema. Version numbers never increase (user rule, 2026-10-09): a format change wipes storage at a
rollout that stops every member before any new build starts, so peers of an old build never meet this one, and
peer protocol 1.0, catalog feed/forwarding 1 and WAN 1 need not tell them apart.

Deployment resets the verified affected databases, system catalog, backup objects and external records.
Existing sessions, tokens, keys, cursors and versions may be invalidated. Startup recreates system/bootstrap
state; service bindings and keys are issued again before auth/dashboard restart. Old snapshots must never
be restored into this format. The PR records exact verified targets and what was actually deleted.
