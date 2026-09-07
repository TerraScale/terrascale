# TerraScale docs (`ts-docs`)

Product docs site source: runnable snippets, `llms.txt`, launch-post drafts,
and pricing/limits pages generated from the checked-in pricing data.

Single authority for product decisions is the workspace-root `GOAL.md`; this
repo holds docs source only.

## Develop

```bash
npm ci
npm run dev
```

## Verify (`make check`)

```bash
npm ci && npm run build
```

`npm test` runs runnable snippets against a real sandbox branch and fails
closed when its credentials are missing. Set the URL, API key, and database ID:

```bash
TERRASCALE_SANDBOX_URL=https://<branch>.<region>.api.terrascale.tech \
TERRASCALE_API_KEY=ts_... \
TERRASCALE_DATABASE_ID=db_... \
npm test
```

For the deterministic local harness, use the explicit stub mode:

```bash
npm run test:stub
```
