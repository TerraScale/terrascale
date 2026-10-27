---
title: Code examples
---

<!-- i18n:fallback:ru -->
> **Перевод на проверке.** У этой страницы пока нет проверенного человеком перевода на русский, поэтому показан английский оригинал.

<div lang="en" dir="ltr">

# Code examples

Use the [JavaScript quickstart](/ru/getting-started/javascript) for the publicly installed `@terrascale/sdk@0.0.17` package. Download [quickstart.mjs](/examples/terrabase-js/quickstart.mjs) and [package.json](/examples/terrabase-js/package.json) for its standalone consumer.

The [C# quickstart](/ru/getting-started/csharp) builds the SDK package from source and includes a full CRUD/query example.

## Example tests

The JavaScript example's tests run against synthetic responses, not the hosted API. Use synthetic data and a test database when you run it for real.

The files under `docs/snippets` use `createTerraBaseV1Client` from `@terrascale/sdk` 0.0.4: document CRUD, SQL, the changes feed, backups and restore, branches and a database per tenant. Backups, branches and tenants need a token with management access to the project. `exports.ts` needs `database(name).export()` from `@terrascale/sdk` 0.0.5, published on npm; see [Export your data](/ru/guides/export). No CI job executes or type-checks the files under `docs/snippets`.

</div>
