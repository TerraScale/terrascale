# Drizzle sqlite-proxy

Source: [Drizzle guide](https://docs.terrascale.tech/guides/orm-drizzle).
This adapter is source-only until the next SDK release.

## 1. Install and connect

In a TerraScale checkout's sdk/js directory run npm install and npm pack.
Install the resulting tarball in the app using its actual path:

~~~sh
npm install drizzle-orm
npm install /path/to/terrascale-sdk-<version>.tgz
~~~

The adapter accepts an SDK database handle, not a Postgres connection string.
Export TERRASCALE_PROJECT_ID, TERRASCALE_DATABASE_NAME and TERRASCALE_API_KEY.
Pass the API key to the required source SDK: it exchanges and refreshes short-lived
access tokens automatically for the existing ORM client. Keep the key on the
application server.
Source: [JavaScript authentication](https://docs.terrascale.tech/getting-started/javascript).

~~~js
import { drizzle } from "drizzle-orm/sqlite-proxy";
import { createTerraBaseClient } from "@terrascale/sdk";
import { drizzleProxy } from "@terrascale/sdk/drizzle";
import { integer, sqliteTable, text } from "drizzle-orm/sqlite-core";

const { query, batch } = drizzleProxy(createTerraBaseClient({
  baseUrl: "https://api.terrascale.tech",
  project: process.env.TERRASCALE_PROJECT_ID,
  apiKey: process.env.TERRASCALE_API_KEY,
}).database(process.env.TERRASCALE_DATABASE_NAME));
const orm = drizzle(query, batch);
const users = sqliteTable("users", {
  id: integer("id").primaryKey(),
  name: text("name").notNull(),
});
~~~

## 2. Define schema and apply migrations

Install the generator in the app:

~~~sh
npm install --save-dev drizzle-kit
~~~

Move the sqlite-core imports and users definition into schema.js and export users.
Save the generator configuration as drizzle.config.js:

~~~js
import { defineConfig } from "drizzle-kit";

export default defineConfig({
  dialect: "sqlite",
  schema: "./schema.js",
  out: "./drizzle",
});
~~~

Generate SQL:

~~~sh
npx drizzle-kit generate
~~~

Source: [Drizzle Kit generate](https://orm.drizzle.team/docs/drizzle-kit-generate).
Review SQL under drizzle/ and copy it into the next ts migrations/NNNN_name.sql
file, then apply
with ts migrate up <preview>. The TypeScript/JavaScript model defines queries;
ts owns applied schema history. Do not also use a Drizzle migration runner.

## 3. Verify and observe limits

After applying the users table:

~~~js
await orm.insert(users).values({ id: 1, name: "Ada" });
const all = await orm.select().from(users);
~~~

Use orm.batch for atomic multi-statement writes. The proxy's orm.transaction
sends begin/statements/commit as separate requests and does not provide an atomic
transaction. Drizzle Kit push and schema introspection are unsupported because
they require catalog access unavailable to this SQL API.
Source: [adapter and migrations](https://docs.terrascale.tech/guides/orm-drizzle).
