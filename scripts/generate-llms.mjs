import { readFileSync, writeFileSync } from 'node:fs';

const text = `# TerraScale documentation

> TerraScale is moving to SQL. Guides are being updated; this map does not
> establish hosted availability or SDK compatibility.

- [Documentation](https://docs.terrascale.tech/)
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
