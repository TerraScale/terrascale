# `create-terrascale-app`

Scaffold a Next.js app that reads and writes TerraScale documents with the
published [`@terrascale/sdk`](https://www.npmjs.com/package/@terrascale/sdk).

## Use it

The package is not published to npm yet, so run the scaffolder from a checkout
of this repository. Use Node.js 22 or 24 and npm 11 or newer, as required by
the generated app's SDK:

```bash
git clone https://github.com/TerraScale/ts-docs.git
node ts-docs/create-terrascale-app/bin/create-terrascale-app.mjs my-app
cd my-app
npm install
cp .env.example .env.local   # set the API key, project ID and database name
npm run dev
```

[Getting started](https://docs.terrascale.tech/getting-started/) shows how to
create the database and the API key. The generated `README.md` explains the
app.

## What it scaffolds (`template/`)

- `app/page.tsx`: lists the newest posts and adds posts through a server action.
- `terrascale.ts`: server-only client. It exchanges the API key for an access
  token and creates the `posts` collection on first use.
- `.env.example`: the three server-only settings.
- `AGENTS.md`: SDK brief for AI assistants working in the app.
