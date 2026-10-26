---
title: Getting started
---

<!-- i18n:fallback:de -->
> **Übersetzung ausstehend.** Diese Seite wurde noch nicht auf Deutsch geprüft und zeigt den englischen Originalinhalt.

<div lang="en" dir="ltr">

# Getting started

Create an account in the [TerraScale dashboard](https://dash.terrascale.tech/register), create an API key, then run the [JavaScript quickstart](/de/getting-started/javascript). It creates, reads, updates, queries and deletes one synthetic document in your database.

## Choose your path

| Language | Start here | Availability |
| --- | --- | --- |
| JavaScript / TypeScript | [JavaScript quickstart](/de/getting-started/javascript) | `@terrascale/sdk@0.0.16` is published on npm |
| C# | [C# quickstart](/de/getting-started/csharp) | Package built from source; not on nuget.org yet |
| Go | [Go quickstart](/de/getting-started/go) | `go get terrascale.tech/sdk@v0.0.6` |
| Python | [Python quickstart](/de/getting-started/python) | `pip install` from the `v0.0.1` Git tag; not on PyPI yet |
| Rust | [SDK reference](/de/guides/sdks#rust) | Git dependency; not on crates.io yet |

There is no Java SDK yet; call the HTTP API directly from those languages.

## Create an account, a database and an API key

1. Open [dash.terrascale.tech/register](https://dash.terrascale.tech/register), select **Create account**, and enter the code sent to your email.
2. Finish setup: choose a plan (or continue without a payment method), then name your first project and pick its region.
3. Open **Databases**, select **New database**, enter a name starting with a lowercase letter (then lowercase letters, digits and inner hyphens), and keep the default of all serving regions or explicitly select at least two. Single-region databases are not supported. Select **Create database**.
4. Open **API keys**, select **Create credential**, then **API key**. Name it, limit **Database access** to that database, choose **Read and write** and select **Create key**. Copy the key when the dashboard shows it and keep it in your secret manager.
5. Open **Organization** and copy the **Current project** ID.

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

The token lasts 60 seconds (`expires_in: 60`) and carries the key's scopes, such as `documents:read documents:write`. Run the exchange again when requests start answering `401`. [Authentication](/de/guides/authentication) explains the contract.

## Write and read a document with curl

With the token exported, keep its header out of process arguments using Bash process substitution. Create a collection, write a document and read it back. Use the project ID and database name from the dashboard.

```bash
export TERRASCALE_PROJECT_ID='YOUR_PROJECT_ID' TERRASCALE_DATABASE_NAME='YOUR_DATABASE_NAME'
DB="https://api.terrascale.tech/v1/projects/$TERRASCALE_PROJECT_ID/databases/$TERRASCALE_DATABASE_NAME"
AUTH="Authorization: Bearer $TERRASCALE_ACCESS_TOKEN"
curl -sS --fail-with-body -H @<(printf '%s\n' "$AUTH") -H 'Content-Type: application/json' \
  -d '{"name":"customers"}' "$DB/collections"  # 409 name_taken when it already exists
curl -sS --fail-with-body -H @<(printf '%s\n' "$AUTH") -H 'Content-Type: application/json' \
  -d '{"email":"team@example.com","seats":12}' "$DB/collections/customers/documents"
curl -sS --fail-with-body -H @<(printf '%s\n' "$AUTH") "$DB/collections/customers/documents?limit=10"
```

The write answers `201` with the new document's `id` and `version`; `GET $DB/collections/customers/documents/by-key?id=<id>` reads one document. The [JavaScript quickstart](/de/getting-started/javascript) runs the full create, read, update, query and delete journey.

## After the first document

Read [queries and pagination](/de/guides/queries), [errors and safe retries](/de/guides/errors), and [replication boundaries](/de/concepts/replication) before building an application that depends on their behavior. [Dashboard usage](/de/guides/dashboard) covers the rest of the customer workflow.

</div>
