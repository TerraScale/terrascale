# Service deletion outbox (TS-989)

This additive SDK contract was assigned after account-deletion PR #62 merged.
The matching auth producer and dashboard consumer are being implemented together;
SDK HTTP-handler tests do not establish deployed producer parity. Existing receipt
and sign-in operations keep their contracts. Package version 0.0.11 gives this
change a distinct immutable identity; main already owns 0.0.10 for file storage.

`auth.Platform.ListDeletionsAsync(clientId, clientSecret, ct)` sends one
`GET /v1/{configured-instance-slug}/deletions` with HTTP Basic client credentials.
A 200 JSON page contains only `items`, at most 20 proofs, each with `id`, `scope`,
`subjectId`, and `organizationId`. Every identity is a canonical uppercase ULID.
Account scope requires null organization; organization scope requires organization
equal to subject; project scope requires a non-null organization ULID. Unknown
scopes, fields, missing fields, malformed identities and wrong bindings fail closed.
No cursor or automatic pagination is introduced; acknowledge processed proofs and
request the next bounded batch explicitly.

After durable consumer cleanup, pass the unchanged proof to
`auth.Platform.AcknowledgeDeletionAsync(clientId, clientSecret, proof, ct)`.
It sends one `POST /v1/{slug}/deletions/{proof.Id}/acknowledge`, with JSON
`{scope,subjectId,organizationId}` (including null for account, never `id`).
Only 204 succeeds. The producer compares the exact proof atomically and handles
matching repeat acknowledgements idempotently; the SDK never retries a request.
A 401 or 404 throws `TerraScaleAuthException` and does not prove acknowledgement.

These operations require authorized service client credentials. They use neither
user bearers, deletion receipts nor the configured management API key. Keep client
secrets out of logs; never treat validation as authorization. The existing bounded
response reader, request deadline, cancellation and source-generated JSON apply.
Caller-owned HTTP handlers must disable redirects and cookies as documented in
`contracts/auth/README.md`. No production calls, deployment or storage change is
part of this SDK PR. The owning auth contract must land before SDK merge/release.

The parent change will qualify and build `TerraScale.DotNet.0.0.11.nupkg` from
the merged SDK PR #63 SHA, then vendor those exact bytes into dashboard PR #211
with matching provenance and lockfile updates. Do not vendor a branch build as
the merged artifact. This handoff requires no NuGet publication or release tag.
