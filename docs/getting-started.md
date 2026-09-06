# Getting started

From `npx create-terrascale-app` to two browser tabs attached to the same live
result view in under five minutes (D-74), on a cloud development branch. The
CLI and SDK need an API key; TerraScale does not ship a customer-run emulator
or local database process.

```bash
npx create-terrascale-app@latest my-app
cd my-app
npm install
cp .env.example .env.local
# install the Go CLI from a ts-cli checkout or release
terrascale dev <database> --watch=false --no-log-tail
npm run dev
```

`terrascale dev` creates or reuses a cloud branch. It prints the branch name,
source, and selected region; it does not print credentials or a database id.
Use the dashboard/API response to obtain the branch database id, then put that
id, the API URL, and a scoped API key in `.env.local`. The starter subscribes
with the schema-bound, read-only SQL builder over CDN-backed HTTP and writes
posts through the published typed document API. Writes and logical
document/history storage are billed; reads,
queries, egress, delivery, fan-out, and subscribers are free. A write
replicated to another selected region costs one additional write per region.

The CRUD, SQL, and reactive snippets run against a real sandbox branch
pre-filled with the reader's key (D-56). If the sandbox or credentials are
unavailable, `npm test` fails; `npm run test:stub` is local development only and
does not qualify launch. Tenant, branch, backup, and export fixtures remain
source-only until their producer contracts are published. Try CRUD:

<<< ./snippets/crud.ts

Next: [Concepts](./concepts.md) · [Runnable snippets](./snippets.md) · [Pricing](./pricing.md).
