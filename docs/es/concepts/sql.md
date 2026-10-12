---
title: SQL compatibility
---

<!-- i18n:fallback:es -->
> **Traducción pendiente.** Esta página aún no tiene revisión humana en español y muestra el contenido original en inglés.

<div lang="en" dir="ltr">

# SQL compatibility

Use [JSON filter queries](/es/guides/queries) for the current project-scoped Documents API. A SQL builder is not its query grammar.

The published JavaScript package has a separate `@terrascale/sdk/sql` entrypoint. Its presence does not establish compatibility with a provisioned endpoint, live subscriptions or the older starter's unpublished dependency.

Before using a separate SQL API, verify its versioned public route, allowed statements, parameters, authorization and response format against the serving release. The current document quickstarts do not advertise SQL execution, DDL, cross-database transactions or automatic indexing.

</div>
