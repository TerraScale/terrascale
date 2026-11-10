# IDs

TerraScale-generated entity IDs are canonical uppercase ULIDs: 26 Crockford base32 characters, with no prefixes or UUID formatting. They identify organizations, users, projects, databases, credentials, invitations, audit events and backup jobs.

A database has an immutable ID and a mutable display name. Public project database routes accept its validated name or canonical ULID. Guarded operations verify the expected immutable ID before mutation, so deleting and recreating a name never redirects authority to the replacement.

Customer SQL primary keys follow the table's declared SQL types and constraints. Applications can use ULIDs for portable, explicitly assigned keys; SQL does not impose the platform entity-ID contract on every column.

Record identity, version preconditions, project ownership and authorization are separate checks. Knowing an ID grants no access. See [database addressing](database-ulid-addressing.md), [management](management-api.md) and [SQL](sql/native-sql.md).

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
