# Rollout: cross-region replication gru ↔ sjc (TS-643)

The required [operating contract](../zero-configuration.md) discovers regions automatically.
OCI Vault owns runtime secrets; GitHub Actions secrets serve CI. Current deployment
and ts-orch migration limits are recorded in [OCI hosting](../../oci.md). Existing
explicit-profile/checker dependencies do not establish automatic region admission.

## Verification requirements

1. Verify authenticated interregion transport and least-privilege query/response
   grants for every admitted region. Region admission must not bypass residency
   or placement restrictions.
2. Use a dedicated canary project/database, never customer data. Automatic
   provisioning must register consistent project identities and deliver scoped
   credentials through the appropriate secret store without printing them.
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
