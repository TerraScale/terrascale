---
title: Getting started
---

<!-- i18n:fallback:pt-br -->
> **Tradução pendente.** Esta página ainda não passou por revisão humana em português e mostra o conteúdo original em inglês.

<div lang="en" dir="ltr">

# Getting started

Create an account in the [TerraScale dashboard](https://dash.terrascale.tech/register), then create a database and an API key.

Fastest path: install the [CLI](/pt-br/cli), run `ts auth login`, then `ts init` — it creates the database, the scoped API key and a ready-to-use `.env` in one command.

## Create an account, a database and an API key

1. Open [dash.terrascale.tech/register](https://dash.terrascale.tech/register), select **Create account**, and enter the code sent to your email.
2. Finish setup: choose a plan (or continue without a payment method), then name your first project and pick its region.
3. Open **Databases**, select **New database**, enter a name starting with a lowercase letter and containing up to 63 lowercase letters, digits, underscores or hyphens (reserved names con, prn, aux, nul, com0–com9 and lpt0–lpt9 are rejected), and select **Create database**.
4. Open **API keys** and select **Create key**. Name it, limit **Database** to that database, check **sql:read** and **sql:write**. Copy the key when the dashboard shows it and keep it in your secret manager.

The key has the form `<key ID>_<secret>`. It is a client credential, not a bearer token: the API answers `401` when you send it directly.

## Exchange the key for an access token

Use Bash, `curl` and `jq`, with shell tracing off. Every request to the API at `https://api.terrascale.tech` carries a short-lived access token. Get one from the token endpoint with the OAuth 2.0 client credentials grant: the client ID is the part of the key before `_`, and the client secret is the whole key.

```bash
set +x
set -o pipefail
read -rs TERRASCALE_API_KEY &&  # paste the key, then press Enter
TERRASCALE_ACCESS_TOKEN="$(
  printf 'user = "%s:%s"\n' "${TERRASCALE_API_KEY%%_*}" "$TERRASCALE_API_KEY" |
    curl -sS --fail-with-body --config - -d grant_type=client_credentials \
      https://auth.terrascale.tech/terrascale/oauth/token |
    jq -er '.access_token | select(type == "string" and length > 0)'
)" && export TERRASCALE_ACCESS_TOKEN
```

The token lasts 60 seconds (`expires_in: 60`) and carries the key's scopes. Run the exchange again when requests start answering `401`. [Authentication](/pt-br/guides/authentication) explains the contract.

## Query from your application

<SdkQueryTabs />

## Next steps

Run your first query with the [SQL quickstart](/pt-br/concepts/sql#quickstart) from the CLI, psql or HTTP. Read [errors and safe retries](/pt-br/guides/errors) before you build an application that depends on error handling. [Dashboard usage](/pt-br/guides/dashboard) covers the rest of the customer workflow.

</div>
