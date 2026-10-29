---
title: Dashboard usage
---

<!-- i18n:fallback:ja -->
> **翻訳レビュー待ち。** このページはまだ日本語の人力レビューがなく、英語の原文を表示しています。

<div lang="en" dir="ltr">

# Dashboard usage

Use the dashboard's account pages to manage access to TerraScale and your databases.

For team setup, follow [organization roles, invitations and ownership](/ja/guides/organization-access). It covers project-only access, plan capacity and offboarding with API-key rotation.

## Prepare a database connection

1. Sign in at [dash.terrascale.tech](https://dash.terrascale.tech/login), or [create an account](https://dash.terrascale.tech/register).
2. Select the organization and project you want to use. The **Organization** page shows the **Current project** ID.
3. Open **Databases** and create a database, or select an existing one.
4. Open **API keys**, select **Create credential**, then **API key**. Name it, limit **Database access** to that database and choose the minimum permissions. Select **Create key**.
5. Copy the key when it is shown and place it in your application's secret store.

The application exchanges the key for an access token; see [authentication](/ja/guides/authentication).

## Use the database from an application

Follow the [JavaScript](/ja/getting-started/javascript) or [C#](/ja/getting-started/csharp) quickstart. A customer application stores its own data in its scoped database. Account identity and dashboard records are isolated from customer databases.

## Review usage and destructive actions

Review [pricing](/ja/pricing/) and [limits](/ja/limits) before increasing traffic. Confirm the resource name and scope before a customer deletion, and keep a copy of any data you need to retain.

</div>
