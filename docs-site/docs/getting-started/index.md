---
title: Getting started
---

# Getting started

Create an account in the [TerraScale dashboard](https://dash.terrascale.tech/register), then create a database and an API key.

Fastest path: install the [CLI](/cli), run `ts auth login`, then `ts init` — it creates the database, the scoped API key and a ready-to-use `.env` in one command.

## Create an account, a database and an API key

1. Open [dash.terrascale.tech/register](https://dash.terrascale.tech/register), select **Create account**, and enter the code sent to your email.
2. Finish setup: choose a plan (or continue without a payment method), then name your first project and pick its region.
3. Open **Databases**, select **New database**, enter a name starting with a lowercase letter and containing up to 63 lowercase letters, digits, underscores or hyphens (reserved names con, prn, aux, nul, com0–com9 and lpt0–lpt9 are rejected), and select **Create database**.
4. Open **API keys** and select **Create key**. Name it, limit **Database** to that database, check **sql:read** and **sql:write**. Copy the key when the dashboard shows it and keep it in your secret manager.

The key has the form `<key ID>_<secret>`. It is a client credential, not a bearer token: the API answers `401` when you send it directly. A key may start with `sk_`.

## Exchange the key for an access token

Use Bash, `curl` and `jq`, with shell tracing off. Every request to the API at `https://api.terrascale.tech` carries a short-lived access token. Get one from the token endpoint with the OAuth 2.0 client credentials grant: the client ID is the key ID after an optional `sk_`, and the client secret is the whole key.

```bash
set +x
set -o pipefail
read -rs TERRASCALE_API_KEY &&  # paste the key, then press Enter
ts_token() {
  key="${TERRASCALE_API_KEY#sk_}"
  printf 'user = "%s:%s"\n' "${key%%_*}" "$TERRASCALE_API_KEY" |
    curl -sS --fail-with-body --config - -d grant_type=client_credentials \
      https://auth.terrascale.tech/terrascale/oauth/token |
    jq -er '.access_token | select(type == "string" and length > 0)'
}
export TERRASCALE_ACCESS_TOKEN="$(ts_token)"
```

Each token lasts 60 seconds (`expires_in: 60`) and carries the key's scopes; call `ts_token` again for a fresh one, as in `Authorization: Bearer $(ts_token)`. [Authentication](/guides/authentication) explains the contract.

## Query from your application

<SdkQueryTabs />

## Next steps

Run your first query with the [SQL quickstart](/concepts/sql#quickstart) from the CLI, psql or HTTP. Read [errors and safe retries](/guides/errors) before you build an application that depends on error handling. [Dashboard usage](/guides/dashboard) covers the rest of the customer workflow.

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
