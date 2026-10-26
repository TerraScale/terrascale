---
title: Dashboard usage
---

<!-- i18n:fallback:zh-cn -->
> **翻译待审。** 此页面尚无经人工审校的中文译文，暂显示英文原文。

<div lang="en" dir="ltr">

# Dashboard usage

Use the dashboard's native account pages to manage access to TerraScale. Use TerraBase SDKs for your application's documents.

For team setup, follow [organization roles, invitations and ownership](/zh-cn/guides/organization-access). It covers project-only access, plan capacity and offboarding with API-key rotation.

## Prepare a database connection

1. Sign in at [dash.terrascale.tech](https://dash.terrascale.tech/login), or [create an account](https://dash.terrascale.tech/register).
2. Select the organization and project you want to use. The **Organization** page shows the **Current project** ID.
3. Open **Databases** and create a database, or select an existing one. Creation defaults to all currently serving regions; an explicit placement needs at least two. **All regions: copies follow new regions automatically** also includes regions added later. See [placement](/zh-cn/concepts/replication#placement); single-region databases are not supported.
4. Open **API keys**, select **Create credential**, then **API key**. Name it, limit **Database access** to that database and choose the minimum permissions. Select **Create key**.
5. Copy the key when it is shown and place it in your application's secret store.

The application exchanges the key for an access token and calls `https://api.terrascale.tech`; see [authentication](/zh-cn/guides/authentication).

## Use the database from an application

Follow the [JavaScript](/zh-cn/getting-started/javascript) or [C#](/zh-cn/getting-started/csharp) quickstart. A customer application stores its own data in its scoped database. Account identity and dashboard records are isolated; these guides do not provide access to platform-owned databases.

The dashboard uses TerraBase through SDKs. This docs site adopts its shared visual design, while documenting only public application workflows.

## Review usage and destructive actions

Review [pricing](/zh-cn/pricing/) and [limits](/zh-cn/limits) before increasing traffic or selecting placement. Confirm the resource name and scope before a customer deletion. [Export data you need to retain](/zh-cn/guides/export) and verify the serving release's deletion and recovery behavior. A document tombstone is not a promise of immediate physical erasure.

</div>
