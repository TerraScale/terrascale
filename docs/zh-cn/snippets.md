---
title: Code examples
---

<!-- i18n:fallback:zh-cn -->
> **翻译待审。** 此页面尚无经人工审校的中文译文，暂显示英文原文。

<div lang="en" dir="ltr">

# Code examples

Use the [JavaScript quickstart](/zh-cn/getting-started/javascript) for the publicly installed `@terrascale/sdk@0.0.16` package. Download [quickstart.mjs](/examples/terrabase-js/quickstart.mjs) and [package.json](/examples/terrabase-js/package.json) for its standalone consumer.

The [C# quickstart](/zh-cn/getting-started/csharp) builds the SDK package from source and includes a full CRUD/query example.

## Example tests

The JavaScript example's tests run against synthetic responses, not the hosted API. Use synthetic data and a test database when you run it for real.

The files under `docs/snippets` use `createTerraBaseV1Client` from `@terrascale/sdk` 0.0.4: document CRUD, SQL, the changes feed, backups and restore, branches and a database per tenant. Backups, branches and tenants need a token with management access to the project. `exports.ts` needs `database(name).export()` from `@terrascale/sdk` 0.0.5, published on npm; see [Export your data](/zh-cn/guides/export). No CI job executes or type-checks the files under `docs/snippets`.

</div>
