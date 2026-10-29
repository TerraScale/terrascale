---
title: Dashboard usage
---

<!-- i18n:fallback:zh-cn -->
> **翻译待审。** 此页面尚无经人工审校的中文译文，暂显示英文原文。

<div lang="en" dir="ltr">

# Dashboard usage

Use the dashboard's account pages to manage access to TerraScale and your databases.

For team setup, follow [organization roles, invitations and ownership](/zh-cn/guides/organization-access). It covers project-only access, plan capacity and offboarding with API-key rotation.

## Prepare a database connection

1. Sign in at [dash.terrascale.tech](https://dash.terrascale.tech/login), or [create an account](https://dash.terrascale.tech/register).
2. Select the organization and project you want to use. The **Organization** page shows the **Current project** ID.
3. Open **Databases** and create a database, or select an existing one.
4. Open **API keys**, select **Create credential**, then **API key**. Name it, limit **Database access** to that database and choose the minimum permissions. Select **Create key**.
5. Copy the key when it is shown and place it in your application's secret store.

The application exchanges the key for an access token; see [authentication](/zh-cn/guides/authentication).

## Use the database from an application

Follow the [JavaScript](/zh-cn/getting-started/javascript) or [C#](/zh-cn/getting-started/csharp) quickstart. A customer application stores its own data in its scoped database. Account identity and dashboard records are isolated from customer databases.

## Review usage and destructive actions

Review [pricing](/zh-cn/pricing/) and [limits](/zh-cn/limits) before increasing traffic. Confirm the resource name and scope before a customer deletion, and keep a copy of any data you need to retain.

</div>
