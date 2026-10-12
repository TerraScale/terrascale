# Platform signing keys: fail closed, operator provisioning

ts-auth never creates the platform issuer's (`terrascale`) signing key by itself (COS decision 2026-10-12, reversible).
An empty `signing_keys` collection for the platform issuer means lost or wiped state; a silently created key would sign
tokens that no relying party's cached JWKS holds, and would hide the loss.

## Behaviour

- **Startup** creates ts-auth's collections (idempotent), registers configured OIDC clients, then *checks* the issuer's
  keys. It never creates or replaces one. It retries after 5 s, doubling up to 5 min.
- **No keys:** `/readyz` answers 503 with
  `{"status":"not_ready","error":"no signing keys: operator provisioning required (docs/auth/signing-keys.md)"}`. The
  JWKS and token endpoints answer 503 (`no_signing_keys`), and the hourly scheduled rotation does nothing.
- **Existing keys** are never replaced or changed by startup, restarts or provisioning. Rotation adds keys only to a
  ring whose keys all verify.
- **Unusable keys** (tampered metadata, another master key) fail closed: not ready, signing answers 500. Nothing replaces
  them; an operator must remove them.
- **Project tenants** (`/<project id>`) still get their first key on first use. Their project record, checked before any
  tenant request, gates them; a wiped project answers 404.
- TerraBase's platform seed still creates the empty `platform-auth` database; it holds no key material.

## Provisioning (operator only; agents never run it)

Run it once per empty platform database, from a machine with Fly SSH access to `ts-auth`:

```sh
fly ssh console -a ts-auth -r gru -C "/app/ts-auth provision-signing-key"
```

- It runs as a separate process on the machine, with the machine's secrets (`TS_AUTH_MASTER_KEY`), and does not serve.
- It creates the collections (idempotent), then the first key **only if there is none**.
- Exit 0 prints either `created the platform issuer's first signing key` or `the platform issuer already has usable
  signing keys; nothing changed`. Exit 1 prints the failure type: storage unreachable, or unusable keys. Nothing is
  replaced either way.
- Audit: the outcome is logged as `Audit: operator command provision-signing-key …` (the Fly log stream). Fly also records
  the SSH session.
- Running it twice, or on two machines at once, is safe. Concurrent first keys are both kept and published, and every
  replica picks the same signer.

Every replica becomes ready on its next bootstrap retry (at most 5 minutes). A restart (`fly machine restart`) makes it
immediate, and is safe because startup never creates keys.

## Recovery from a wipe

1. Confirm the loss is real: `/readyz` reports the `no signing keys` error, and TerraBase's `platform-auth` database has
   an empty `signing_keys` collection.
2. Decide: tokens signed by the lost keys can no longer be verified, so users sign in again and relying parties refetch
   the JWKS. The JWKS cache lifetime is at most the key-ring refresh interval.
3. Run the provisioning command above once.
4. Check `/readyz` is 200 in every region.

To undo this decision (startup creates the first key again), restore the startup provisioning in `SigningKeyRotation`.
See `KeyRing.ProvisionAsync`.
