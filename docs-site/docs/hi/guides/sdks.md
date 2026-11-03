---
title: SDK reference
description: TerraScale SDKs for JavaScript, C#, Go, Python and Rust, with install commands and quickstarts.
---

<!-- i18n:fallback:hi -->
> **अनुवाद लंबित।** इस पृष्ठ का मानव-समीक्षित हिंदी अनुवाद अभी उपलब्ध नहीं है, इसलिए मूल अंग्रेज़ी सामग्री दिखाई जा रही है।

<div lang="en" dir="ltr">

# SDK reference

Official SDKs for the TerraBase document and SQL API at `https://api.terrascale.tech`. Every SDK exchanges a dashboard API key for short-lived project access tokens (or shows you how to), sends typed requests and preserves the server's error contract. Create a key first: see [Getting started](/hi/getting-started/).

| Language | Package | Install | Quickstart |
| --- | --- | --- | --- |
| JavaScript / TypeScript | `@terrascale/sdk` on npm | `npm install @terrascale/sdk` | [JavaScript quickstart](/hi/getting-started/javascript) |
| C# / .NET | `TerraScale.DotNet` (source; NuGet pending) | `dotnet pack` from the monorepo into a local feed | [C# quickstart](/hi/getting-started/csharp) |
| Go | `terrascale.tech/sdk` | `go get terrascale.tech/sdk@v0.0.8` | [Go quickstart](/hi/getting-started/go) |
| Python | `terrascale` (source; PyPI pending) | `pip install "git+https://github.com/TerraScale/terrascale@main#subdirectory=sdk/python"` | [Python quickstart](/hi/getting-started/python) |
| Rust | `terrascale` (source; crates.io pending) | `terrascale = { git = "https://github.com/TerraScale/terrascale.git" }` in `Cargo.toml` | [Rust README](https://github.com/TerraScale/terrascale/blob/main/sdk/rust/README.md) |

Every SDK covers the same core surface: databases and collections, documents (create/get/put/patch/delete), typed queries with filters, sorting and cursor pages, transactions and relational SQL. Language-specific extras (schema codegen for TypeScript, change streams, shape subscriptions) are documented in each package.

## Which entry do I call?

- **Data operations** go through the SDK's document client against `https://api.terrascale.tech`, with a project access token.
- **Key exchange** happens against `https://auth.terrascale.tech/terrascale/oauth/token` with the client credentials grant. Go, Python and Rust SDKs do it for you; the JavaScript and .NET SDKs expose the token call and a token-provider hook (each quickstart shows the exact code). See [authentication](/hi/guides/authentication).
- **SQL over plain HTTP** (any HTTP client) is documented in [SQL over HTTP](/hi/guides/sql-http); PostgreSQL-compatible clients work too ([PostgreSQL clients](/hi/guides/postgres-clients)).

## Versioning

The SDKs track the TerraBase v1 API and stay on `0.0.x` versions while it evolves; breaking changes are called out in each repository's changelog. The OpenAPI document at [`https://api.terrascale.tech/openapi/v1.json`](https://api.terrascale.tech/openapi/v1.json) is the source of truth for routes and schemas.

</div>
