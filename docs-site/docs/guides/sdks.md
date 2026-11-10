---
title: SDK reference
description: TerraScale SDKs for JavaScript, C#, Go and Python, with install commands and quickstarts. The Rust SDK is not published.
---

# SDK reference

Official SDKs for the TerraBase SQL API at `https://api.terrascale.tech`. Every SDK exchanges a dashboard API key for short-lived project access tokens (or shows you how to), sends typed requests and preserves the server's error contract. Create a key first: see [Getting started](/getting-started/).

| Language | Package | Install | Quickstart |
| --- | --- | --- | --- |
| JavaScript / TypeScript | `@terrascale/sdk` on npm | The published 0.0.17 package is the previous API. See [getting started](/getting-started/javascript). | [JavaScript quickstart](/getting-started/javascript) |
| C# / .NET | `TerraScale.DotNet` (preview; not on NuGet) | preview: `dotnet pack` from the monorepo into a local package folder | [C# quickstart](/getting-started/csharp) |
| Go | `terrascale.tech/sdk` | `go get terrascale.tech/sdk@v0.0.27` | [Go quickstart](/getting-started/go) |
| Python | `terrascale` (source; PyPI pending) | `pip install "git+https://github.com/TerraScale/terrascale@main#subdirectory=sdk/python"` | [Python quickstart](/getting-started/python) |
| Rust | not published (`sdk/rust` remains in the repository) | none | — |

Every SDK runs SQL with the same calls: `execute`, `query` and `batch` post to `/v1/projects/{project}/databases/{database}/sql/execute`, one request and one transaction each, with positional or named arguments and typed rows. The .NET, Go, Python and Rust SDKs also open a WebSocket session on `/sql/ws` for interactive transactions (`begin`, `commit`, `rollback`): `SqlSession` in Python and Rust, the session clients in Go and .NET. The previous published JavaScript package (0.0.17) does not open one. Primary keys are ULIDs from each SDK's ID generator.

## Query from your application

<SdkQueryTabs />

## Which entry do I call?

- **SQL** goes through the SDK's database handle against `https://api.terrascale.tech`, with a project access token.
- **Key exchange** happens against `https://auth.terrascale.tech/terrascale/oauth/token` with the client credentials grant. Go, Python, Rust and .NET do the exchange when you pass the dashboard API key; the previous published JavaScript package (0.0.17) exposes the token call and a token-provider hook (its quickstart shows the code). See [authentication](/guides/authentication).
- **SQL over plain HTTP** (any HTTP client) is documented in [SQL over HTTP](/guides/sql-http); PostgreSQL-compatible clients work too ([PostgreSQL clients](/guides/postgres-clients)).

## Versioning

The SDKs track the TerraBase v1 API and stay on `0.0.x` versions while it evolves; breaking changes are called out in each repository's changelog. The OpenAPI document at [`https://api.terrascale.tech/openapi/v1.json`](https://api.terrascale.tech/openapi/v1.json) is the source of truth for routes and schemas.
