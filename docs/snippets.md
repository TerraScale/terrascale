---
title: Code examples
---

# Code examples

Use the [JavaScript quickstart](/getting-started/javascript) for the publicly installed `@terrascale/sdk@0.0.3` package. Download [quickstart.mjs](/examples/terrabase-js/quickstart.mjs) and [package.json](/examples/terrabase-js/package.json) for its standalone consumer.

The [C# quickstart](/getting-started/csharp) builds the SDK package from source and includes a full CRUD/query example.

## Example tests

The JavaScript example's tests run against synthetic responses, not the hosted API. Use synthetic data and a test database when you run it for real.

Older files under `docs/snippets` and the retained Next.js starter pin `@terrascale/sdk` 0.3.0, which is not published. They target an older contract; do not copy them into new applications.

`docs/snippets/exports.ts` targets the current client instead: it exports a database with `@terrascale/sdk` 0.0.5 `database(name).export()` and resumes from the last line's cursor. See [Export your data](/guides/export). No CI job executes or type-checks the files under `docs/snippets`.
