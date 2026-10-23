---
title: Getting started
---

<!-- i18n:fallback:pt-br -->
> **Tradução pendente.** Esta página ainda não passou por revisão humana em português e mostra o conteúdo original em inglês.

<div lang="en" dir="ltr">

# Getting started

Create an account in the [TerraScale dashboard](https://dash.terrascale.tech/register), create an API key, then run the [JavaScript quickstart](/pt-br/getting-started/javascript). It creates, reads, updates, queries and deletes one synthetic document in your database.

## Choose your path

| Language | Start here | Availability |
| --- | --- | --- |
| JavaScript / TypeScript | [JavaScript quickstart](/pt-br/getting-started/javascript) | `@terrascale/sdk@0.0.3` is published on npm |
| C# | [C# quickstart](/pt-br/getting-started/csharp) | Exact candidate package required; public NuGet release not available |
| Go | [SDK reference](/pt-br/guides/sdks) | Source candidate; immutable module and deployed journey require verification |

Package availability checked October 12, 2026 (UTC). There is no documented public Python, Java or Rust SDK release for this contract.

## Create an account, a database and an API key

1. Open [dash.terrascale.tech/register](https://dash.terrascale.tech/register), create your account and enter the code sent to your email.
2. Finish setup: choose a plan (or continue without a payment method), then name your first project and pick its region.
3. Open **Databases**, select **New database**, enter a name (lowercase letters, digits and inner hyphens) and create it.
4. Open **API keys**, select **New project key**, limit **Database access** to that database, choose **Read and write** and create it. Copy the key when the dashboard shows it and keep it in your secret manager.
5. Open **Organization** and copy the **Current project** ID.

The key has the form `<key ID>_<secret>`. It is a client credential, not a bearer token: the API answers `401` when you send it directly.

## Exchange the key for an access token

Every request to the API at `https://api.terrascale.tech` carries a short-lived access token. Get one from the token endpoint with the OAuth 2.0 client credentials grant: the client ID is the part of the key before `_`, and the client secret is the whole key.

```bash
read -rs TERRASCALE_API_KEY && export TERRASCALE_API_KEY  # paste the key, then press Enter
export TERRASCALE_ACCESS_TOKEN="$(curl -sS --fail-with-body \
  -u "${TERRASCALE_API_KEY%%_*}:$TERRASCALE_API_KEY" -d grant_type=client_credentials \
  https://auth.terrascale.tech/terrascale/oauth/token \
  | sed -nE 's/.*"access_token": *"([^"]+)".*/\1/p')"
```

The token lasts 15 minutes (`expires_in: 900`) and carries the key's scopes, such as `documents:read documents:write`. Run the exchange again when requests start answering `401`. [Authentication](/pt-br/guides/authentication) explains the contract.

## After the first document

Read [queries and pagination](/pt-br/guides/queries), [errors and safe retries](/pt-br/guides/errors), and [replication boundaries](/pt-br/concepts/replication) before building an application that depends on their behavior. [Dashboard usage](/pt-br/guides/dashboard) covers the rest of the customer workflow.

</div>
