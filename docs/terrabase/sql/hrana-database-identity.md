# Hrana batons retain database identity

A Hrana baton pins a stream to its original database ULID, not only its canonical
name. Deleting a database and creating another with the same name must not let an
old inline query or mutation execute against the replacement. Both v2/v3 pipelines
and v3 cursors reject such a baton with HTTP 400 `STREAM_EXPIRED` before executing
any request or emitting cursor steps. A fresh request without a baton can open the
replacement database after normal authentication and authorization.

Batons are opaque to clients. The server now emits a 51-character envelope:
128 random bits encoded as 22 base64url characters, a dot, the canonical uppercase
26-character database ULID, and `.a` for stateless or `.t` for stateful. Invalid,
unbound older envelopes and mismatched identities are refused; no legacy parsing
path exists. Official libSQL clients must return the received string unchanged.
The database identity is an identity pin, not a credential or a signature. Every
request still needs independent project/database authorization. Deliberately
changing an envelope cannot grant access unavailable to the current credential.

Same-database stateless recovery remains available when a stream expires or its
lookup is missing, including when a request reaches another node. A transaction
or stored SQL still cannot recover from a missing stateful stream. Credential and
read/write-right checks on taking existing streams remain unchanged. Rotation,
stream shares, idle expiry and close/disposal use the existing stream lifecycle.

The added real-listener Fast cases cover stateless, stored-SQL and transaction
batons, both before and after controlled expiry, across database delete/recreate;
they assert typed refusal, fresh-stream success and unchanged replacement rows.
Cursor cases require refusal before batch output. Malformed/unbound/foreign pins
cannot write; same-identity expiry and missing-stream controls still write/read
successfully. Each case owns its server, credentials and data with no sleeps.
These cases require CI execution; source preparation does not establish runtime
or live compatibility. No storage format or request/response schema changes.
