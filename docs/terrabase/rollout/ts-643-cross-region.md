# Rollout: cross-region replication gru-1 ↔ sfo-1 (TS-643)

This replaces the retired manual region-list and credential-copy rollout
recipe. The required [operating contract](../zero-configuration.md) discovers
every region automatically and uses OCI Vault for canary and transport secrets.
The current runtime and checker still depend on explicit profiles and a canary
file; removing those dependencies is pending TS-773/TS-796.

## Verification requirements

1. Verify authenticated interregion transport and least-privilege query/response
   grants for every admitted region. Region admission must not bypass residency
   or placement restrictions.
2. Use a dedicated canary project/database, never customer data. Automatic
   provisioning must register consistent project identities and deliver scoped
   credentials through Vault without printing or copying them.
3. Verify that each direction's writes become readable with the same version and
   that concurrent writes converge. Missing membership, lag, malformed responses
   or unverified credentials fail the observation.
4. Retain artifact-integrity, signer-trust and rollback checks. Delete artifacts
   only after proving no member or rollback reference needs them; this document
   authorizes no deletion or live operation.

The legacy checker emits one JSON observation and distinguishes verification
failure from an unreadable or insecure canary input. Its prior two-region
observations do not establish automatic discovery, a complete serving inventory
or a continuous clean soak.
