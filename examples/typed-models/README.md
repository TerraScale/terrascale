# Typed document models

Requires Node 22 or 24 and npm 11+. Download all six files linked below into one directory.

[package.json](./package.json) · [schema.json](./schema.json) · [verify-models.mjs](./verify-models.mjs) · [model-types.ts](./model-types.ts) · [tsconfig.json](./tsconfig.json) · [README.txt](/examples/typed-models/README.txt)

```sh
npm install
npm run check
```

The manifest pins published SDK 0.0.16 and generator peer 16.0.0. `generate` uses the installed `terrascale-models` CLI; verification compares it with two `generateDocumentModels` calls. Generated `models.ts` is reproducible; regenerate when the declaration changes. Keep tooling out of browser bundles.

Strict compilation covers required quantity, nullable note, exact numeric values, optional-field deletion, and seven expected type errors. An unused `@ts-expect-error` fails compilation. Range and integer constraints still require server validation: TypeScript accepts negative/fractional numbers. Reads remain JSON; generated aliases do not validate historical data or narrow a read automatically. `id` and `version` remain envelope fields.

## Run against your own database

Also download [live.ts](./live.ts) here and the [server provider module](/examples/server-token-provider/server-client.mjs) into `../server-token-provider/server-client.mjs`. That is the maintained renewing provider, shared with the server example. `npm run check` compiles the live flow as well; `npm run live` executes it.

Supply `TERRASCALE_API_KEY`, `TERRASCALE_PROJECT_ID`, `TERRASCALE_DATABASE_NAME` and your existing authorized `TERRASCALE_API_URL` privately through the server environment. Use a project key with `documents:read documents:write` and an isolated existing database. Never place these values in browser output, source files or CLI arguments. Automatic endpoint discovery has not shipped; reuse the endpoint your authorized application already uses.

The flow creates a unique collection, declares the downloaded schema, checks typed create/read/merge-patch/ops-patch/delete and server range rejection, then drops only its own confirmed collection in `finally` with a fresh cleanup deadline. It does not provision/delete a database. Operations are sent once. On an ambiguous collection-create response, reconcile the printed unique name through the collection catalog before cleanup; do not replay the mutation. A failed cleanup must be resolved before considering the run complete. This fixture's publication does not claim a live run.
