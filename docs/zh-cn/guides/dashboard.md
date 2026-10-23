---
title: Dashboard usage
---

<!-- i18n:fallback:zh-cn -->
> **翻译待审。** 此页面尚无经人工审校的中文译文，暂显示英文原文。

<div lang="en" dir="ltr">

# Dashboard usage

Use the dashboard's native account pages to manage access to TerraScale. Use TerraBase SDKs for your application's documents. Public self-service access and individual dashboard workflows must be confirmed for the serving release.

## Prepare a database connection

1. Sign in through the supported dashboard entrypoint supplied with your access.
2. Select the organization and project you are authorized to use.
3. Select or provision an isolated development database through the available public workflow.
4. Obtain its database name, collection name and HTTPS API endpoint.
5. Issue or obtain a scoped application credential through the supported access flow and place it in your application's secret store.

Do not infer a credential issuance route or a UI control from this checklist. Where provisioning or authorization is unavailable, stop before database requests; a local test is not a substitute for an allocation.

## Use the database from an application

Follow the [JavaScript](/zh-cn/getting-started/javascript) or [C#](/zh-cn/getting-started/csharp) quickstart. A customer application stores its own data in its scoped database. Account identity and dashboard records are isolated; these guides do not provide access to platform-owned databases.

The dashboard uses TerraBase through SDKs. This docs site adopts its shared visual design, while documenting only public application workflows.

## Review usage and destructive actions

Review [pricing](/zh-cn/pricing/) and [limits](/zh-cn/limits) before increasing traffic or selecting placement. Confirm the resource name and scope before a customer deletion. [Export data you need to retain](/zh-cn/guides/export) and verify the serving release's deletion and recovery behavior. A document tombstone is not a promise of immediate physical erasure.

</div>
