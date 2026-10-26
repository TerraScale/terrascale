# Operator API keys

`POST /admin/api-keys` issues a `tb_` key on the internal administrative listener.
It accepts only `documents:read` and `documents:write`, with a required
`organizationId`, optional `projectId`, and optional `databaseId` requiring the
project. Its response includes the key's `id` and one-time `secret` at the top level.

`DELETE /admin/api-keys/{key}` revokes an operator key by its canonical ULID.
It returns 204 after anchoring the revocation, 404 for an unknown key, 400 for a
malformed id, or 503 if catalog verification or anchoring fails. The route is
unavailable on the client listener. Subsequent requests with the key fail authentication;
a poll already admitted retains its existing live-timeout bound.

Key issuance stores both directions of the hash lookup so revocation can remove
the credential and its lookup atomically, including organization-wide keys.
Project closure retains its existing batch revocation and project-key bookkeeping.
