import { readFileSync, writeFileSync } from 'node:fs';

const text = `# TerraScale documentation

> TerraScale hosts SQL databases in the SQLite dialect. Each database lives in
> one home region. Exchange a dashboard API key for a short-lived access token
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

- [Documentation](https://docs.terrascale.tech/)
- [Getting started](https://docs.terrascale.tech/getting-started/)
- [SQL overview](https://docs.terrascale.tech/concepts/sql)
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
