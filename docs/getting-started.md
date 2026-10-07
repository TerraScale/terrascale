# Getting started

::: warning Legacy Next.js reference
This page retains the `@terrascale/sdk` 0.3.0 starter contract. That pin is
not published on npm; the published `@terrascale/sdk@0.0.2` does not establish
compatibility with these examples. Stub success does not qualify the
sign-in or live-update journey. For the project-scoped C# TerraBase candidate,
use the [C# quickstart](/getting-started/csharp) and
[SDK compatibility](/guides/sdks). Automatic Atlas integration in the current
candidates is deferred; configure an explicit endpoint or resolver as described
in [Atlas discovery](/guides/atlas).
:::

The target is to go from `npx create-terrascale-app` to two browser tabs
attached to the same live result view in under five minutes (D-74), on a cloud
development branch. This target remains unqualified until the real sign-in,
branch, and live-update journey passes. The CLI and SDK need an API key;
TerraScale does not ship a customer-run emulator or local database process.

```bash
npx create-terrascale-app@latest my-app
cd my-app
npm install
cp .env.example .env.local
# build the .NET CLI from a ts-cli checkout and put `terrascale` on PATH
terrascale dev <database> --watch=false --no-log-tail
# Fill in the public project, branch, API, and registered callback settings.
terrascale push --database <branch-database>
npm run dev
```

`terrascale dev` creates or reuses a cloud branch. It prints the branch name,
source, and selected region; it does not print credentials or a database id.
Use the dashboard/API response to obtain the branch database id, then put that
id and the public project, API, auth, and registered callback settings in
`.env.local`. Export `TERRASCALE_API_KEY` separately for the CLI; Next.js
settings do not configure the shell. Click **Sign in with TerraScale** in
each browser tab. The legacy starter uses OAuth PKCE and exchanges the signed-in
credential for short-lived access scoped to the selected branch and its
`posts` collection. Credentials remain in tab memory; never put privileged
keys in `NEXT_PUBLIC` settings. The deployed provider must accept the exact
registered callback and authorize that branch. The starter subscribes
with the schema-bound, read-only SQL builder over CDN-backed HTTP and writes
posts through its retained typed document facade. These legacy APIs are not
the candidate project-scoped Documents contract. Reads and writes
consume operation units (each started 4 KiB read or written; writes count once
per selected Region, reads only in the serving Region), and logical
document/history storage counts in every selected Region, against the
organization’s shared allowance.

The retained CRUD, SQL, and reactive snippets target a legacy sandbox contract
(D-56). `npm test` and `npm run test:stub` run the deterministic local smoke
harness; neither qualifies launch. `npm run test:remote` is the explicit
credential-gated remote lane and fails when its sandbox or credentials are
unavailable. It does not qualify the candidate Documents API. Tenant and
project-scoped branch/backup fixtures await producer contracts.
The legacy contract records database-scoped branch, backup, and export routes;
the export
fixture remains source-only pending qualification. Try CRUD:

<<< ./snippets/crud.ts

Next: [Concepts](./concepts.md) · [Runnable snippets](./snippets.md) · [Pricing](./pricing.md).
