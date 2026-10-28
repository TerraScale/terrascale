# TS-754 Auth cutover

Auth uses the maintained `TerraScaleIdGenerator` from the pinned `ts-dotnet`
SDK for monotonic canonical uppercase ULIDs. User, organization, project,
invitation, audit, signing-key, API-key client, OIDC authorization and OIDC token identifiers
use that generator. API-key credentials are `<26-character ULID>_<52-character
random secret>`; only their digest is stored. Opaque session and invitation
bearer secrets remain cryptographically random secrets.

The dashboard bootstrap client id is `01M4EQ2FRJ8S8B29E8TQVWNW4J`.
`TS_AUTH_SERVICE_CLIENTS` accepts canonical ULID client ids. Client grants,
audiences, live token/authorization entries and authentication epochs distinguish
user sign-ins from client credentials; differing id formats are not an
authorization boundary.

This preparation is not the completed storage cutover. The current storage
fixture still uses the old backend. Direct document-key identity, membership
and session document ids, native uniqueness and fresh startup provisioning must
be qualified together against the combined schema10 backend before activation.
Persisted entity payloads must omit root `id`; responses must recover it from
the document envelope. The UUIDv8 addressing and `uniques` claim implementation
must be deleted in that conversion.

The exact prepared schema API is in SDK commit
[`1ce73bfdadd1d7e2b3d0451768388b08b4811bbb`](https://github.com/TerraScale/ts-dotnet/commit/1ce73bfdadd1d7e2b3d0451768388b08b4811bbb),
against TerraBase PR 118 revision
`1c2a7e082829bcebf816691537001f6aba168d9a`. Neither is a qualified schema10
fixture/release pin. Native uniqueness is atomic locally and resolves replica
conflicts eventually; it does not supply exclusive admission or fenced failover.

Activation requires a producer-supported TerraBase-issued bootstrap credential
for schema PUT/GET, an approved exclusive-admission design and its partition/
failover proof, qualified backend/SDK pins, and the TS-703/TS-745 secret/profile
rollout. Auth must not bootstrap with a token it signed itself. The TS-577
unfenced automatic failover exception cannot authorize credential effects.

The accepted rollout is a clean reset: fence old writers, verify the exact Auth
database/collection targets, delete their old state, recreate bootstrap state,
then qualify GRU/SFO sign-in and TerraBase JWT acceptance before reopening.
Preserve master-key bytes and derivation with TS-703/TS-745. Signing keys use
canonical ULID `kid` values, including the signing keys in `signing_keys`. Noncanonical key
identities fail closed at runtime; the existing bootstrap replaces an invalid
ring of the configured master key with a fresh ring, without relabelling keys.
The sealing context, authenticated metadata and rotation policy are unchanged.
A ring naming another master key is never replaced by a misconfigured replica.
No migration, backfill, id rewrite, compatibility path or legacy fallback is
permitted. No production data has been deleted by this preparation.
