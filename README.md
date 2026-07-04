# TerraScale TypeScript SDK

This repository contains the `@terrascale/sdk` package.

The package is the customer-facing TypeScript client for TerraScale public REST
APIs. Dashboard-only and owner/admin helper APIs are kept behind explicit
internal entrypoints so the public package surface stays clear.

## Entry Points

- `@terrascale/sdk` exports the SDK client, REST models, ULID helpers, runtime
  config, HTTP primitives, and result types.
- `@terrascale/sdk/internal/dashboard` is reserved for Dashboard/admin
  consumption and is not part of the advertised customer API.

## Development

```bash
npm install
npm run typecheck
npm run build
```

Integration tests are expected to run against a real TerraScale AppHost stack
and its emitted API/Auth URLs:

```bash
TERRASCALE_API_BASE_URL=http://127.0.0.1:... \
TERRASCALE_AUTH_API_BASE_URL=http://127.0.0.1:... \
npm run test:integration
```

When those URLs are not set, integration tests skip instead of substituting
mocked HTTP.
