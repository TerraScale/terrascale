# Replace PlanetScale and mysql2 clients

These are server-side examples. Keep credentials in environment/secret stores;
do not send privileged database keys to browsers. The application must authorize
each operation separately: [TerraScale authentication][auth]. Use the translated
`customers` schema from [the mapping reference](mysql-type-and-syntax-mapping.md).
The queries below deliberately return IDs as decimal text for a stable app DTO.

## 1. Before: @planetscale/database

The PlanetScale serverless driver is a Vitess/MySQL HTTP client, not a generic
HTTP transport. Changing its endpoint cannot make it a TerraScale client.
See [PlanetScale's driver guide][ps-driver] and
[driver README](https://github.com/planetscale/database-js).

```js
import { connect } from "@planetscale/database";

const source = connect({
  host: process.env.PLANETSCALE_HOST,
  username: process.env.PLANETSCALE_USERNAME,
  password: process.env.PLANETSCALE_PASSWORD,
});

const result = await source.execute(
  "SELECT CAST(id AS CHAR) AS id, email FROM customers WHERE email = ? LIMIT 1",
  [process.env.CUSTOMER_EMAIL],
);
const customer = result.rows[0] ?? null;
```

This lookup uses the source column's collation. The replacement queries a
precomputed `email_key` so comparison semantics are explicit.

## 2. After: SQL over HTTP for serverless runtimes

Use Node.js 22/24 or another runtime providing `fetch`. Exchange the API key using
the documented [client-credentials flow][auth]. This example reads a freshly
supplied `TERRASCALE_ACCESS_TOKEN`, which lives at most 60 seconds; the calling
server must obtain a current token before invoking it: [SQL HTTP][http].
Do not use an API key directly as a bearer token. This is a standalone raw HTTP
integration, not a wrapper around or bypass of a TerraScale SDK.

```js
const required = name => {
  const value = process.env[name];
  if (!value) throw new Error(`Missing ${name}`);
  return value;
};

const project = encodeURIComponent(required("TERRASCALE_PROJECT_ID"));
const database = encodeURIComponent(required("TERRASCALE_DATABASE_NAME"));
const endpoint = `https://api.terrascale.tech/v1/projects/${project}/databases/${database}/sql/execute`;
const response = await fetch(endpoint, {
  method: "POST",
  headers: {
    Authorization: `Bearer ${required("TERRASCALE_ACCESS_TOKEN")}`,
    "Content-Type": "application/json",
  },
  body: JSON.stringify({
    statements: [{
      sql: "SELECT CAST(id AS TEXT) AS id, email FROM customers WHERE email_key = ? LIMIT 1",
      args: [required("CUSTOMER_EMAIL_KEY")],
    }],
  }),
});
if (!response.ok) throw new Error(`SQL request failed with status ${response.status}`);
const payload = await response.json();
const result = payload.results[0];
const customer = result.rows.length === 0 ? null : Object.fromEntries(
  result.columns.map((column, i) => [column.name, result.rows[0][i]]),
);
```

HTTP results contain `columns` metadata and row arrays, unlike PlanetScale's
object rows. Positional `?` parameters are sent in `args`; a named map is also
supported. Response fields include `rows_affected` and `last_insert_rowid`:
[SQL HTTP][http]. Handle non-2xx responses before decoding results. Production
error handling should retain only a sanitized code/status and correlation data;
never log the raw body, headers or customer values: [authentication][auth].

Raw JSON numbers cannot safely round-trip every 64-bit integer through JavaScript
`Number`. This read casts the ID to text in SQL. For writes requiring large
integers, use the documented SDK's `bigint` encoding below or a separately
verified exact JSON encoder/decoder. Do not call `JSON.stringify` on a bigint or
silently convert it to Number: [JS quickstart][js], [SQL value types][sql].

## 3. Before: mysql2/promise

MySQL2 exposes promise connections and prepared execution. Decimal/bigint options
must preserve source precision; this read also selects the ID as text:
[MySQL2 quickstart][mysql2], [MySQL2 options][mysql2-options].
`MYSQL_CA_PEM` is certificate material supplied by configuration, not a password.

```js
import mysql from "mysql2/promise";

const source = await mysql.createConnection({
  host: process.env.MYSQL_HOST,
  user: process.env.MYSQL_USER,
  password: process.env.MYSQL_PASSWORD,
  database: process.env.SOURCE_DATABASE,
  ssl: { ca: process.env.MYSQL_CA_PEM, rejectUnauthorized: true },
  supportBigNumbers: true,
  bigNumberStrings: true,
  dateStrings: true,
});

try {
  const [rows] = await source.execute(
    "SELECT CAST(id AS CHAR) AS id, email FROM customers WHERE email = ? LIMIT 1",
    [process.env.CUSTOMER_EMAIL],
  );
  const customer = rows[0] ?? null;
} finally {
  await source.end();
}
```

## 4. After: TerraScale JavaScript SDK

The current [JS quickstart][js] describes source version 0.0.19 and identifies
0.0.17 as the previous published package. Its example uses
`createTerraBaseV1Client`; do not assume that export exists in an older package.
For this example, install from a reviewed source checkout as documented:

```sh
npm install /path/to/terrascale/sdk/js
```

Pin the selected source revision in your dependency process. If source
installation is unacceptable, use the documented HTTP route above or a supported
Postgres driver with SQLite-dialect SQL: [Postgres clients][pg].

```js
import { createTerraBaseV1Client } from "@terrascale/sdk";

const required = name => {
  const value = process.env[name];
  if (!value) throw new Error(`Missing ${name}`);
  return value;
};

const db = createTerraBaseV1Client({
  baseUrl: "https://api.terrascale.tech",
  project: required("TERRASCALE_PROJECT_ID"),
  apiKey: required("TERRASCALE_API_KEY"),
}).database(required("TERRASCALE_DATABASE_NAME"));

const rows = await db.query(
  "SELECT CAST(id AS TEXT) AS id, email FROM customers WHERE email_key = ? LIMIT 1",
  [required("CUSTOMER_EMAIL_KEY")],
);
const customer = rows[0] ?? null;
```

The client exchanges and refreshes the API key's access token. `query` returns
objects keyed by column name; `execute` and `batch` use SQL result metadata.
Pass large integers as `bigint`; integers above 2^53 decode as `ExactDecimal`:
[JS quickstart][js]. Audit your DTO serializer rather than assuming numeric rows
are interchangeable with mysql2's configured strings.

## 5. Replace a MySQL upsert and transaction

Source SQL, where `email` has the source unique collation:

```sql
INSERT INTO customers (id, email, balance, status)
VALUES (?, ?, ?, ?)
ON DUPLICATE KEY UPDATE balance = VALUES(balance), status = VALUES(status);
```

After translation, choose `email_key` as the conflict target explicitly. Supply
exact cents as a bigint and a validated application-generated UTC timestamp:

```js
await db.batch([{
  sql: "INSERT INTO customers (id, email, email_key, balance_cents, status, updated_at) VALUES (?, ?, ?, ?, ?, ?) ON CONFLICT(email_key) DO UPDATE SET balance_cents = excluded.balance_cents, status = excluded.status, updated_at = excluded.updated_at",
  args: [
    BigInt(required("CUSTOMER_ID")),
    required("CUSTOMER_EMAIL"),
    required("CUSTOMER_EMAIL_KEY"),
    BigInt(required("CUSTOMER_BALANCE_CENTS")),
    required("CUSTOMER_STATUS"),
    required("CUSTOMER_UPDATED_AT_UTC"),
  ],
}]);
```

On a conflict involving a different unique key, expect a constraint error rather
than assuming MySQL's any-unique-key behavior. The caller owns the ID high-water
and normalization policy. The conflict action preserves the existing row's ID.
See [MySQL upsert semantics](https://dev.mysql.com/doc/refman/8.4/en/insert-on-duplicate.html)
and the [mapping reference](mysql-type-and-syntax-mapping.md).

Replace `beginTransaction`/`commit` over HTTP with a single `db.batch` containing
the full atomic unit. Do not send BEGIN/COMMIT through `execute`. For read-dependent
interactive logic, use the documented WebSocket or Postgres-wire transaction
surface and keep it within 5 seconds: [SQL][sql], [limits][limits].
If the unit cannot fit within statement, row-change, byte and execution limits,
redesign the business operation before cutover; splitting a transaction changes
its atomicity. Do not replay uncertain writes blindly: [error recovery][errors].

Prisma and Drizzle migrations require different providers, schema definitions
and adapters; use the main skill's ORM checklist and the public
[Prisma guide](https://docs.terrascale.tech/guides/orm-prisma) and
[Drizzle guide](https://docs.terrascale.tech/guides/orm-drizzle).

[auth]: https://docs.terrascale.tech/guides/authentication
[http]: https://docs.terrascale.tech/guides/sql-http
[sql]: https://docs.terrascale.tech/concepts/sql
[js]: https://docs.terrascale.tech/getting-started/javascript
[pg]: https://docs.terrascale.tech/guides/postgres-clients
[limits]: https://docs.terrascale.tech/limits
[errors]: https://docs.terrascale.tech/guides/errors
[ps-driver]: https://planetscale.com/docs/vitess/tutorials/using-the-serverless-driver
[mysql2]: https://sidorares.github.io/node-mysql2/docs
[mysql2-options]: https://sidorares.github.io/node-mysql2/docs/api-and-configurations
