# TerraScale identity bootstrap

All ts-auth application records live in TerraBase's pre-created `ts-auth` database, in relational tables written
through the TerraBase EF Core provider over TerraBase's private listener ([storage](storage.md)). TerraBase independently seeds its catalog before
identity exists; ts-auth never creates its database or reads the dashboard's database.

| Record | Fixed ULID | Name / owner |
| --- | --- | --- |
| Organization | 00000000000000000000000001 | TerraScale |
| Auth project | 00000000000000000000000002 | ts-auth |
| Auth database | 00000000000000000000000003 | ts-auth; owned by auth project |
| Dashboard database | 00000000000000000000000004 | ts-dashboard; owned by dashboard project |
| Dashboard project | 00000000000000000000000005 | ts-dashboard |

The initial passwordless user is `mariogk@terrascale.tech`, owner of the organization and explicit admin of both projects.
The account has no password or authentication bypass: email-code sign-in still proves control of its mailbox through
OCI Email Delivery with Cloudflare fallback. `TS_AUTH_SMTP_USERNAME`, `TS_AUTH_SMTP_PASSWORD`,
`CLOUDFLARE_ACCOUNT_ID` and `CLOUDFLARE_EMAIL_TOKEN` come from OCI Vault.

Every startup applies pending EF Core migrations, creates the owner unless its normalized email already has an account,
and creates the organization, two projects, roles and audit event in one create-only request. Concurrent starts read the
winner. Restarts preserve account credentials, role revocations and existing identity records. Conflicting IDs or an
incomplete existing topology fail closed and are never overwritten. Restore inconsistent records from an approved
recovery copy; startup is not a privilege-restoration operation.

The auth database is homed in gru with no replicas; the dashboard database keeps the same shape in its own seeding
region. Outside gru, the deploy specification sets `TS_AUTH_TERRABASE_URL` to an address that reaches gru's TerraBase
internal listener over the private network, chosen with the new fleet's networking. It uses gru's TerraBase peer
secret (`gru-1-terrabase-peer-secret`); every replica uses the one database in gru. That deploy specification changes
separately from this PR. Outside Development, storage readiness requires `X-TerraBase-Region: gru` on the conditional
probe's storage responses; a missing header or another region refuses readiness. Every storage request carries
`TerraScale-Write: home`. Security
decisions and conditional writes stay on that one home writer, with no failover, to preserve the existing uniqueness,
revocation and single-use behavior. A gru outage stops sign-in everywhere until step 2: a readable copy per region
with a promotable home. Authority still follows the SQL storage and credential-fence contract.

## Production proof after deployment

Verify the serving node and region using the deployed OCI ingress and private
identity contract; do not infer region from a hostname alone. Complete an
email-code sign-in in each serving region, then read `/v1/terrascale/organizations`
and its projects as the owner. Obtain each required code from the mailbox owner
for the exact URL and verification step; never log codes, cookies or tokens.

Read the identity records written through gru from gru's private TerraBase listener with the SDK and the
orchestrator's approved private access. Auth
responses alone do not prove the storage path because security reads run at the home.
The concrete ingress and private listener access paths must be verified during
migration; these requirements are not evidence of a completed production run.
