# Prisma SQLite adapter

Source: [Prisma guide](https://docs.terrascale.tech/guides/orm-prisma).
The adapter export is in the next SDK release; install sdk/js from a source
checkout rather than assuming the published package contains it.

## 1. Install and define the model

Install @prisma/client and the source SDK in the application; install the Prisma
CLI as described in [Prisma setup](https://www.prisma.io/docs/orm/tools/prisma-cli).

~~~sh
npm install @prisma/client /path/to/terrascale/sdk/js
npm install --save-dev prisma
~~~

Use the SQLite datasource in prisma/schema.prisma. Prisma 7 needs neither
previewFeatures nor a datasource URL when the adapter supplies the connection.
A minimal model matching the guide's person writes:

~~~prisma
generator client {
  provider = "prisma-client-js"
}
datasource db {
  provider = "sqlite"
}
model Person {
  id    Int    @id
  name  String
  score Float
}
~~~

Generate the client with npx prisma generate before importing it.
Source: [Prisma client generation](https://www.prisma.io/docs/orm/prisma-client/setup-and-configuration/generating-prisma-client).

## 2. Connect

Export TERRASCALE_PROJECT_ID, TERRASCALE_DATABASE_NAME and TERRASCALE_API_KEY.
Pass the API key to the required source SDK: it exchanges and refreshes short-lived
access tokens automatically for the existing ORM client. Keep the key on the
application server.
Source: [JavaScript authentication](https://docs.terrascale.tech/getting-started/javascript).

~~~js
import { PrismaClient } from "@prisma/client";
import { createTerraBaseClient } from "@terrascale/sdk";
import { PrismaTerraScale } from "@terrascale/sdk/prisma";

const db = createTerraBaseClient({
  baseUrl: "https://api.terrascale.tech",
  project: process.env.TERRASCALE_PROJECT_ID,
  apiKey: process.env.TERRASCALE_API_KEY,
}).database(process.env.TERRASCALE_DATABASE_NAME);
const prisma = new PrismaClient({ adapter: new PrismaTerraScale(db) });
~~~

## 3. Generate SQL; let ts own application

~~~sh
npx prisma migrate diff --from-empty --to-schema prisma/schema.prisma --script > migration.sql
~~~

For later diffs, the guide uses --from-migrations prisma/migrations instead of
--from-empty. Keep that generator history aligned if using it. Review generated
SQL, put it into a numbered ts migration file, and apply to a preview.
Prisma migrate dev, db push and their shadow database are unsupported.
Source: [Prisma migrations](https://docs.terrascale.tech/guides/orm-prisma).

## 4. Verify

After migration:

~~~js
await prisma.person.create({ data: { id: 1, name: "Ada", score: 1.5 } });
const people = await prisma.person.findMany();
~~~

Interactive $transaction callbacks use one SQL WebSocket session with a
five-second transaction limit. Unique violations map to P2002, not-null to P2011,
foreign-key to P2003, missing tables to P2021 and missing columns to P2022.
Source: [Prisma transactions and errors](https://docs.terrascale.tech/guides/orm-prisma).
