# TerraScale identity bootstrap

All ts-auth application records live in TerraBase's pre-created `ts-auth` database, through the ts-dotnet SDK and
TerraBase's private listener. There is one collection per entity type. TerraBase independently seeds its catalog before
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
Cloudflare Email Sending. `CLOUDFLARE_ACCOUNT_ID` and `CLOUDFLARE_EMAIL_TOKEN` come from OCI Vault.

Every startup creates missing collections, claims the owner's normalized email, and creates the organization, two
projects, roles, unique project-name claims and audit event in one create-only transaction. Concurrent starts read the
winner. Restarts preserve account credentials, role revocations and existing identity records. Conflicting IDs or an
incomplete existing topology fail closed and are never overwritten. Restore inconsistent records from an approved
recovery copy; startup is not a privilege-restoration operation.

Both databases have gru as home and sjc/fra replicas. ts-auth serves in all three regions; discovery and JWKS read the
local replica with private DNS fallback. Security decisions and conditional writes still use gru's TerraBase authority,
with no failover, to preserve the existing uniqueness, revocation and single-use behavior. An unavailable gru authority
makes sign-in fail closed across all regions. This change does not implement lattice-native distributed authentication.
