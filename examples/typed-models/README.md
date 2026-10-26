# Typed document models

Requires Node 22 or 24 and npm 11+. Download all six files linked below into one directory.

[package.json](./package.json) · [schema.json](./schema.json) · [verify-models.mjs](./verify-models.mjs) · [model-types.ts](./model-types.ts) · [tsconfig.json](./tsconfig.json) · [README.txt](/examples/typed-models/README.txt)

```sh
npm install
npm run check
```

The manifest pins published SDK 0.0.13 and generator peer 16.0.0. `generate` uses the installed `terrascale-models` CLI; verification compares it with two `generateDocumentModels` calls. Generated `models.ts` is reproducible; regenerate when the declaration changes. Keep tooling out of browser bundles.

Strict compilation covers required quantity, nullable note, exact numeric values, optional-field deletion, and seven expected type errors. An unused `@ts-expect-error` fails compilation. Range and integer constraints still require server validation: TypeScript accepts negative/fractional numbers. Reads remain JSON; generated aliases do not validate historical data or narrow a read automatically. `id` and `version` remain envelope fields.
