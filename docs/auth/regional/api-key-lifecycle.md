# API key lifecycle

This note replaces the "Delegated API-key secrets" part of
[SQL credential fences](../pivot/p1-sql-storage.md).

## A key works until it is revoked or expires

An API key is an organization's or project's credential, not a copy of the
sign-in that created it. Earlier, each key secret stored its creator's
sign-in lineage (`secretBindings`, `previousSecretBindings`) and the token
endpoint refused the key once any part of that lineage stopped being the
regional winner. The lineage included the creator's authentication state and
dashboard sign-in, so ordinary events (signing out everywhere, a password
reset or recovery sign-in, the dashboard sign-in being revoked) silently
broke every key that user had created, and the dashboard still listed them
as Active.

Now:

- Creation and rotation still require a sign-in that is the current regional
  winner, so a sign-in that already lost cannot issue a secret.
- After that, a key secret is accepted while the key record exists, has not
  expired, and the secret matches the current digest (or the previous one
  during its rotation grace). The record's lineage fields are no longer
  written or read; stored ones are ignored.
- Revocation deletes the record, which replicates to every region.

## Revoked keys stop within 60 seconds

TerraBase validates ts-auth JWTs offline. Organization and project tokens
(API-key exchanges, user token exchanges) now live 60 seconds
(`PlatformGrants.Lifetime`), so a revoked key, a deleted project or a deleted
organization stops working at TerraBase in every region within 60 seconds of
the change. The SDK token sources read `expires_in` and refresh before expiry.

## Last use

`api_key_usage` holds one record per key: `keyId`, `organizationId`,
`projectId` and `lastUsedAt`. A successful client-credentials exchange
updates it at most once per 5 minutes, best effort: a failed or lost write
never fails the exchange. It is a separate table so a usage write can
never race the key record itself, where an unrelated usage update must not undo a
rotation or a revocation. API key bodies return it as `last_used_at`. Deleting
an organization or project removes its usage records with its keys.

`api_key_usage` is a new required table. ts-auth creates it at startup,
so no storage format change or data reset is needed.
