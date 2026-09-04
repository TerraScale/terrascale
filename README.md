# TerraScale docs (`ts-docs`)

Product docs site source: runnable snippets (D-56), `llms.txt`, launch-post
stubs (L-5), and the pricing/limits pages generated from D-16/D-17/D-21/D-25/D-64 (L-1 draft).

Single authority for product decisions is the workspace-root `GOAL.md`; this
repo holds docs source only.

## Develop

```bash
npm ci
npm run docs:dev
```

## Verify (`make check`)

```bash
npm ci && npm run build && npm test
```

`npm test` runs the snippet harness in stub-sandbox mode (no credentials
needed). Against a real sandbox branch:

```bash
TERRASCALE_SANDBOX_URL=https://<branch>.<region>.api.terrascale.tech \
TERRASCALE_API_KEY=ts_... \
npm test -- --sandbox=remote
```
