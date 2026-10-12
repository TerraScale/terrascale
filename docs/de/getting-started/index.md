---
title: Getting started
---

<!-- i18n:fallback:de -->
> **Übersetzung ausstehend.** Diese Seite wurde noch nicht auf Deutsch geprüft und zeigt den englischen Originalinhalt.

<div lang="en" dir="ltr">

# Getting started

Install the published JavaScript SDK and run the [JavaScript quickstart](/de/getting-started/javascript). It creates, reads, updates, queries and deletes one synthetic document in a database you are authorized to use.

::: warning Service availability
TerraScale is preparing for public self-service launch. A published SDK does not establish hosted provisioning, dashboard sign-in or production authorization availability. Obtain a provisioned endpoint and scoped credential through the supported access process before running database requests.
:::

## Choose your path

| Language | Start here | Availability |
| --- | --- | --- |
| JavaScript / TypeScript | [JavaScript quickstart](/de/getting-started/javascript) | `@terrascale/sdk@0.0.3` is published on npm |
| C# | [C# quickstart](/de/getting-started/csharp) | Exact candidate package required; public NuGet release not available |
| Go | [SDK reference](/de/guides/sdks) | Source candidate; immutable module and deployed journey require verification |

Package availability checked October 12, 2026 (UTC). There is no documented public Python, Java or Rust SDK release for this contract.

## Prepare an application

1. Obtain a project ID, database name and collection name for an isolated development allocation.
2. Obtain its HTTPS API endpoint and a credential authorized for document reads and writes.
3. Install the exact SDK version from the relevant guide.
4. Run the example with synthetic data and verify its success message.

Database handles do not provision identity resources. Keep application data in TerraBase and use the supported identity flow for users and organizations. [Authentication](/de/guides/authentication) explains credential boundaries; [dashboard usage](/de/guides/dashboard) explains the customer workflow.

## After the first document

Read [queries and pagination](/de/guides/queries), [errors and safe retries](/de/guides/errors), and [replication boundaries](/de/concepts/replication) before building an application that depends on their behavior.

</div>
