# Platform signing keys: startup provisioning

Every ts-auth replica uses the pre-created TerraBase database `ts-auth` through ts-dotnet. Startup creates the
collections, seeds the TerraScale owner and projects, registers OIDC clients, then provisions the platform issuer's
first signing key only when the ring is empty. The generated ES256 private key is encrypted with `TS_AUTH_MASTER_KEY`,
which is supplied by OCI Vault. Private key material and the master key never appear in responses or logs.

## Behaviour

- Startup is idempotent: existing keys are checked under the master key and are never replaced.
- Startup retries storage failures after 5 seconds, doubling up to 5 minutes. `/readyz` remains 503 until the identity
  bootstrap and key verification complete; TerraBase can start before or after ts-auth.
- Tampered keys or a different master key fail closed. Startup does not replace unusable keys.
- Concurrent initial starts may generate multiple keys; all remain published and replicas select the same signer.
- Scheduled rotation adds keys after 30 days, with a two-day publication overlap. Project tenants receive their first
  key only while their project exists at the storage authority.

`ts-auth provision-signing-key` remains an idempotent diagnostic command.
Normal startup requires no operator provisioning step.

## Recovery implications

An empty ring receives a new key on restart. This is the explicit greenfield startup policy: it does not recover lost
keys. Tokens signed by missing keys cannot be verified, so users must sign in again. Preserve the database and master
key together for recovery. Changing the master key without restoring matching encrypted records prevents readiness.

TerraBase's [replica rebuild recovery](../terrabase/replica-rebuild.md) distinguishes clean shutdown from abrupt
process termination. Clean restart preserves the same stored signing key. An unclean restart refuses an uncertain
auth copy and retains its original database/WAL; ts-auth stays unready until certified recovery succeeds. Recovery
requires an intact placed peer and verified retained state; without a certifiable source the copy remains refused.

See [platform-bootstrap.md](platform-bootstrap.md) for identity IDs and region verification.
