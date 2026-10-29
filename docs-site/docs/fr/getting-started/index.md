---
title: Getting started
---

<!-- i18n:fallback:fr -->
> **Traduction en attente.** Cette page n’a pas encore été relue en français et affiche le contenu original en anglais.

<div lang="en" dir="ltr">

# Getting started

Create an account in the [TerraScale dashboard](https://dash.terrascale.tech/register), then create a database and an API key.

## Create an account, a database and an API key

1. Open [dash.terrascale.tech/register](https://dash.terrascale.tech/register), select **Create account**, and enter the code sent to your email.
2. Finish setup: choose a plan (or continue without a payment method), then name your first project and pick its region.
3. Open **Databases**, select **New database**, enter a name starting with a lowercase letter (then lowercase letters, digits and inner hyphens), and select **Create database**.
4. Open **API keys**, select **Create credential**, then **API key**. Name it, limit **Database access** to that database, choose **Read and write** and select **Create key**. Copy the key when the dashboard shows it and keep it in your secret manager.

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

The token lasts 60 seconds (`expires_in: 60`) and carries the key's scopes. Run the exchange again when requests start answering `401`. [Authentication](/fr/guides/authentication) explains the contract.

## Next steps

Read [SQL](/fr/concepts/sql) for the query interface, and [errors and safe retries](/fr/guides/errors) before you build an application that depends on error handling. [Dashboard usage](/fr/guides/dashboard) covers the rest of the customer workflow.

</div>
