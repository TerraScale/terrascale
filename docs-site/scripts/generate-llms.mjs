import { readFileSync, writeFileSync } from 'node:fs';

const text = `# TerraScale documentation

> TerraScale hosts SQL databases in the SQLite dialect. A database lives in a set of
> regions, at least one, and every region of the set accepts reads and writes. The default TerraScale-Durability is local.
> Reads are served in the region you call. Postgres and WebSocket sessions preserve read-your-writes; REST reads are eventually consistent.
> A read can lag behind writes ordered in another region of the set. A write sent to another region is forwarded, or committed locally and confirmed.
> Exchange a dashboard API key for a short-lived access token
> at https://auth.terrascale.tech/terrascale/oauth/token (client credentials
> grant), then reach the database three ways:
>
> - HTTP: POST https://api.terrascale.tech/v1/projects/{project}/databases/{database}/sql/execute
>   with {"statements":[{"sql","args","named"}]}. One request is one
>   transaction of 1 to 256 statements, all or nothing.
> - WebSocket: GET .../databases/{database}/sql/ws, subprotocol
>   terrascale.sql.v1, for interactive transactions of at most 5 seconds.
> - Postgres wire: postgresql://{database}@pg.terrascale.tech:5432/{database}
>   with the API key as the password. Statements stay in the SQLite dialect.

> Live shapes support where filters and selected columns. End-user tokens need table read grants.
> Agent databases support TTL, forks and database-scoped keys. Point-in-time recovery creates a new database;
> in-place restore is coming. Database deletion destroys backup data keys; physical cleanup is separate.
> Plans enforce write caps and SQL read fair use. Customer-set hard dollar spending caps are coming.
> Copies in the other regions of a database's set are not billed separately. No production replication-lag guarantee is published.

- [Documentation](https://docs.terrascale.tech/)
- [Getting started](https://docs.terrascale.tech/getting-started/)
- [SQL overview](https://docs.terrascale.tech/concepts/sql)
- [Regions](https://docs.terrascale.tech/concepts/regions)
- [Replication](https://docs.terrascale.tech/concepts/replication)
- [Multi-region writes and conflicts](https://docs.terrascale.tech/guides/multi-region-writes-and-conflicts)
- [Agent databases](https://docs.terrascale.tech/guides/agent-databases)
- [Pricing](https://docs.terrascale.tech/pricing)
- [SQL over HTTP](https://docs.terrascale.tech/guides/sql-http)
- [Postgres clients](https://docs.terrascale.tech/guides/postgres-clients)
- [Authentication](https://docs.terrascale.tech/guides/authentication)
- [SDKs](https://docs.terrascale.tech/guides/sdks)
`;
const output = new URL('../docs/public/llms.txt', import.meta.url);
if (process.argv.includes('--check')) {
  if (readFileSync(output, 'utf8') !== text) throw new Error('Public map is stale');
} else {
  writeFileSync(output, text);
}
