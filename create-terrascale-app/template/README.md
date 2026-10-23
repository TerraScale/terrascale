# Starter app (from `create-terrascale-app`)

Next.js + [`@terrascale/sdk`](https://www.npmjs.com/package/@terrascale/sdk).
The home page lists the newest posts in your database and adds new ones
through a server action. Everything runs against the TerraScale cloud; there is
no customer-run emulator or local database process.

## Run it

1. Create an account, a database and a project API key limited to that
   database with **Read and write**, as in
   [Getting started](https://docs.terrascale.tech/getting-started/). Copy the
   **Current project** ID from **Organization**.
2. `npm install`
3. `cp .env.example .env.local`, then fill in `TERRASCALE_API_KEY`,
   `TERRASCALE_PROJECT_ID` and `TERRASCALE_DATABASE` (the database name).
4. `npm run dev` and open http://localhost:3000. Add a post.

The app creates the `posts` collection on first use.

## How it works

- `terrascale.ts` runs on the server only. It exchanges the API key for a
  15-minute access token with the OAuth 2.0 client credentials grant, renews it
  before expiry, and returns the `posts` collection from
  `createTerraBaseV1Client`.
- `app/page.tsx` reads with `collection.query()` and writes with
  `collection.create()`, which assigns a ULID document id.

Keep the API key server-side. Never put it in a `NEXT_PUBLIC_` setting.
