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

`make check` (and `npm test`) runs the deterministic local stub harness. This
checks the build and runnable snippet behavior without cloud credentials.

The explicit remote qualification lane runs against a real sandbox branch and
fails closed unless all sandbox credentials are supplied:

```bash
TERRASCALE_SANDBOX_URL=https://<branch>.<region>.api.terrascale.tech \
TERRASCALE_SANDBOX_API_KEY=ts_... \
TERRASCALE_SANDBOX_DATABASE_ID=db_... \
make qualify-remote
```

For the deterministic local harness, use the explicit stub mode:

```bash
npm run test:stub
```

The stub harness is local smoke evidence only; it never substitutes for the
remote qualification lane.
