---
title: SQL compatibility
---

<!-- i18n:fallback:hi -->
> **अनुवाद लंबित।** इस पृष्ठ का मानव-समीक्षित हिंदी अनुवाद अभी उपलब्ध नहीं है, इसलिए मूल अंग्रेज़ी सामग्री दिखाई जा रही है।

<div lang="en" dir="ltr">

# SQL compatibility

Use [JSON filter queries](/hi/guides/queries) for the current project-scoped Documents API. A SQL builder is not its query grammar.

The published JavaScript package has a separate `@terrascale/sdk/sql` entrypoint. Its presence does not establish compatibility with a provisioned endpoint, live subscriptions or the older starter's unpublished dependency.

Before using a separate SQL API, verify its versioned public route, allowed statements, parameters, authorization and response format against the serving release. The current document quickstarts do not advertise SQL execution, DDL, cross-database transactions or automatic indexing.

</div>
