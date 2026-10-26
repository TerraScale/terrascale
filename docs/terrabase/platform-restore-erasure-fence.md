# Platform sources and whole-database restore

The authenticated whole-backup and point-in-time restore APIs reject sources in the
platform organization, including the canonical `ts-auth` and `ts-dashboard` databases:

- `POST /v1/projects/{project}/databases/{database}/backups/{backup}/restore`
- `POST /v1/projects/{project}/databases/{database}/point-in-time/restore`

After source authorization, a valid restore request returns HTTP 409 with code
`recovery_erasure_fenced` and message `platform database historical recovery is fenced by erasure requirements`.
This is the selective historical recovery refusal contract. It applies before backup
lookup, download, staging, import or destination creation, for both platform and
customer backup destinations. The PITR implementation also enforces the source fence.

The refusal is unconditional: missing erasure receipts cannot establish safety while
erasure is pending or SYSTEM replication is asynchronous. An independently restored
platform copy would fall outside the canonical erasure rewrite. See [history erasure](history-erasure.md).

Customer organization sources retain the existing restore behavior, authorization,
retention, history validation and database allowance checks. This changes no request or
response shape, storage format or replication protocol; it adds the 409 refusal for
platform sources. Separately reviewed operator disaster recovery remains a distinct
procedure; these APIs provide no override for platform historical copies.

Fast integration coverage: `PlatformRestoreFenceTests` exercises both platform sources
through the TLS client listener with scoped global-issuer tokens and valid backups,
with and without a receipt. Existing `BackupApiTests`, `RestoreWindowTests`,
`CustomerBackupTests` and `PointInTimeEntitlementTests` cover allowed customer restores
and their access, retention, history and quota boundaries.
