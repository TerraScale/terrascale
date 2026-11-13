# Agent databases

The database list, overview and Settings show the expiry reported by TerraBase.
A database without expiry displays `Never`.

Create accepts a blank TTL for a permanent database or a TTL in hours. Fork
copies the current database when Point in time is blank; otherwise it restores
retained platform backup history at the selected UTC time into a new named
database. Fork accepts its own TTL; it does not inherit the source expiry.
Both operations consume the existing plan's normal database and storage allowance.

Settings can set TTL from now or clear expiry. TTL is between one minute and
365 days (60–31,536,000 seconds). Fractional hours are accepted and rounded to
the nearest second. Invalid values fail before a producer mutation. Setting TTL
retains management authorization and CSRF checks. Expiry scheduling and deletion
are owned by TerraBase.

MCP exposes `create_database(name, ttlHours?)`,
`fork_database(databaseId, name, pointInTime?, ttlHours?)`,
`set_database_ttl(databaseId, ttlHours)` or
`set_database_ttl(databaseId, clear: true)`, `run_sql(databaseId, sql)` and
`delete_database(databaseId, confirmName)`. `pointInTime` is an ISO 8601 timestamp;
the native form uses UTC. With `pointInTime`, `fork_database` starts a point-in-time
restore and returns the running operation at once instead of waiting for the copy;
poll `get_restore_status` for the new database, and the dashboard fork form redirects
to the backups tab with the operation's progress. Without it, the fork returns the
ready database. `run_sql` remains read only. MCP deletion returns `step_up_required`
with the database ID and a scoped dashboard `confirmationUrl`; API keys, OAuth and
personal access tokens never delete through MCP. The database remains until a
signed-in user completes dashboard deletion with its existing confirmation controls. `fork_database` replaces `branch_database`; only the new tool is exposed.
Database MCP results include `expiresAt`, explicitly null for permanent databases.

The session and CSRF protected `POST /v1/databases/ttl` form accepts `databaseId`
and `ttlHours`, or `databaseId` and `action=clear`. Its endpoint is included in
TerraScale's generated OpenAPI specification and Scalar reference. Dashboard calls
MagicOnion management and backups contracts for these customer databases; the RPC
services call TerraBase's public `/v1/projects/{project}/databases` routes. The
dashboard's own records stay in its relational tables ([state storage](state-storage.md)).

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
