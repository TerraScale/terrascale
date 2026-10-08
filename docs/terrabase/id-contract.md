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

Fresh storage uses data-directory layout 2, SQL schema 9 and catalog version 6. New files bootstrap the full
schema atomically. Old nonempty files and unsupported schemas are refused. There are no migrations,
backfills, dual reads/writes or legacy ID fallbacks. TS-755 extends fresh SQL bootstrap to schema 10 with
native uniqueness before the combined production rollout. Peer protocol 2.0, catalog feed/forwarding version 2
and WAN version 2 refuse old peers before interpreting their UUID wire bytes as ULIDs.

Deployment resets the verified affected databases, system catalog, backup objects and external records.
Existing sessions, tokens, keys, cursors and versions may be invalidated. Startup recreates system/bootstrap
state; service bindings and keys are issued again before auth/dashboard restart. Old snapshots must never
be restored into this format. The PR records exact verified targets and what was actually deleted.
