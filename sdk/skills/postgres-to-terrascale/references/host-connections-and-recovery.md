# Host connections, branches and recovery

Use the relevant section before inventory or export. Keep source and destination
credentials in separate services/secret bindings. Establish the network route
first, then verify identity and read permissions with the source psql connection.
The migration does not require public exposure of a private database.

## Neon

- In the Neon console, select the exact project, branch and database being moved.
  Record their nonsecret identifiers in the migration manifest. A different branch
  can have the same table names but different data.
- Obtain a **direct/unpooled** connection string for psql, snapshot synchronization
  and pg_dump. The pooled hostname contains `-pooler`; use the console's direct
  string rather than editing or printing a secret URL. Neon's transaction pooler
  is not the export endpoint. Sources:
  [pg_dump migration](https://neon.com/docs/import/migrate-from-postgres),
  [pooling](https://neon.com/docs/connect/connection-pooling).
- Inventory all branches and decide which divergent datasets are needed. Neon
  branches are isolated clones; do not assume branch history migrates in the SQL
  dump. See [Neon branching](https://neon.com/docs/introduction/branching).
- Import each independently needed dataset through its own verified export. For
  new previews of an already imported TerraScale database, use the destination's
  independent snapshot branches:

```sh
ts db branch create migration-check --from "$TARGET_DATABASE" --ttl 7d
ts db branch list
```

TerraScale branch writes remain isolated; each branch consumes a database slot.
The default TTL is seven days, and expiry permanently deletes that branch. Choose
a TTL covering rehearsal and rollback needs. Branch naming and credentials are
documented in [branches](https://docs.terrascale.tech/guides/branches). Branches
created from the imported baseline do not recreate divergent Neon branch data.

## AWS RDS and Aurora PostgreSQL

- Use the instance endpoint for RDS or the Aurora cluster's writer endpoint for
  the authoritative source. Confirm endpoint, database and port in the console.
  Export from an allowed runner in the VPC or through approved VPN/bastion access;
  security groups, route tables and firewalls must permit the connection. Do not
  make a private instance public to run pg_dump. Sources:
  [RDS connections](https://docs.aws.amazon.com/AmazonRDS/latest/UserGuide/USER_ConnectToPostgreSQLInstance.html),
  [Aurora connections](https://docs.aws.amazon.com/AmazonRDS/latest/AuroraUserGuide/Aurora.Connecting.html).
- Use TLS certificate verification with the provider's current CA and documented
  endpoint hostname. If IAM database authentication is enabled, confirm the IAM
  permission, mapped database user and database grants. Tokens expire after
  15 minutes for new connections; obtain a fresh token for each new connection
  as needed and place it only in the protected credential binding. It does not
  replace the database user's SELECT privileges. Sources:
  [IAM connection requirements](https://docs.aws.amazon.com/AmazonRDS/latest/UserGuide/UsingWithRDS.IAMDBAuth.Connecting.html),
  [IAM with psql](https://docs.aws.amazon.com/AmazonRDS/latest/UserGuide/UsingWithRDS.IAMDBAuth.Connecting.AWSCLI.PostgreSQL.html).
- Retain an RDS snapshot or Aurora cluster snapshot as a source-side rollback
  asset, and rehearse restoring it to a separate instance/cluster with the needed
  parameter groups and network access. Sources:
  [RDS snapshot restore](https://docs.aws.amazon.com/AmazonRDS/latest/UserGuide/USER_RestoreFromSnapshot.html),
  [Aurora snapshot restore](https://docs.aws.amazon.com/AmazonRDS/latest/AuroraUserGuide/aurora-restore-snapshot.html).

## Google Cloud SQL for PostgreSQL

- Choose a runner with the required private-IP/VPC path, or approved public-IP
  authorized networking. A Cloud SQL Auth Proxy/connector handles the documented
  secure connection; it does not create a private network route. Configure the
  libpq service for that actual endpoint and port. Sources:
  [connection options](https://cloud.google.com/sql/docs/postgres/connect-overview),
  [Auth Proxy](https://cloud.google.com/sql/docs/postgres/sql-proxy).
- For IAM authentication, enable the instance feature, provision the IAM database
  user, grant IAM connection/login rights and SQL read permissions. Prefer
  automatic IAM authentication through the Auth Proxy for psql/pg_dump; configure
  the proxy's documented automatic-IAM mode. Manual OAuth tokens are valid for
  only one hour and must be refreshed for later connections. Sources:
  [IAM authentication](https://cloud.google.com/sql/docs/postgres/iam-authentication),
  [IAM login](https://cloud.google.com/sql/docs/postgres/iam-logins).
- Keep a source backup/recovery point and rehearse Cloud SQL restore separately
  from the export. A provider backup is a source recovery asset, not a portable
  SQL import. See [Cloud SQL restore](https://cloud.google.com/sql/docs/postgres/backup-recovery/restore).

## Azure Database for PostgreSQL

- Identify the server/database and its public-firewall, private endpoint or VNet
  access configuration. Run the exporter on an allowed network path and validate
  TLS against the hostname/provider CA. Do not loosen the firewall globally.
  See [networking](https://learn.microsoft.com/en-us/azure/postgresql/flexible-server/concepts-networking)
  and [TLS](https://learn.microsoft.com/en-us/azure/postgresql/flexible-server/security-connect-tls).
- Use the configured PostgreSQL password or Microsoft Entra identity. For Entra,
  have the administrator provision the database principal and grants; obtain the
  documented token securely and refresh it for new connections as required.
  See [Entra authentication](https://learn.microsoft.com/en-us/azure/postgresql/flexible-server/security-entra-configure).

## Self-hosted and other PostgreSQL providers

Use the actual primary endpoint, approved SSH/VPN path or local Unix socket.
Validate `pg_hba.conf`, TLS and role permissions with the operator; use a direct
connection capable of retaining a snapshot transaction. Do not dump an arbitrary
load-balanced read replica and assume it matches the write primary. See
[client authentication](https://www.postgresql.org/docs/current/client-authentication.html),
[SSL](https://www.postgresql.org/docs/current/libpq-ssl.html) and
[backup approaches](https://www.postgresql.org/docs/current/backup.html).

## Destination backups are a separate recovery system

TerraScale has continuous backup, manual/scheduled snapshots, restore to a new
database and in-place rewind. Read the actual proven point-in-time window and
check backup success; retention is plan-dependent. Rehearse restore to a new
database and verify it before source retirement. An in-place rewind discards later
writes and needs explicit approval for the exact database and time. Sources:
[backups and restore](https://docs.terrascale.tech/guides/backups-and-restore),
[limits](https://docs.terrascale.tech/limits).

RDS/Aurora snapshots, Cloud SQL backups and PostgreSQL physical backups cannot be
uploaded as destination databases. Restore those on the source platform first if
needed, then export and convert logical SQL. TerraScale has no file upload/restore
endpoint for these source artifacts: [import](https://docs.terrascale.tech/guides/import).
