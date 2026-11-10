# Dashboard integration

The dashboard calls TerraBase over MagicOnion through the shared contracts library for database management, SQL, usage and recovery. It calls ts-auth for identity and access. Each product owns its data; the dashboard never reads another service's storage.

The dashboard stores its sessions, SQL workspaces, saved queries and billing state as SQL tables in its own TerraBase database. See [state storage](../dashboard/state-storage.md) and [platform API](../dashboard/platform-api.md).

Database browsing uses SQL table metadata and SQL execution. Schema changes use SQL DDL. [Native SQL](sql/native-sql.md) defines transaction bounds, errors and read/write scopes. The dashboard must preserve upstream authorization, row/byte bounds, cancellation and unknown observations.

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
