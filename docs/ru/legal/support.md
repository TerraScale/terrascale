---
title: Support policy
description: TerraScale support channels — Discord, email, and the Scale shared channel. No ticketing product, no phone.
---

<!-- i18n:fallback:ru -->
> **Перевод на проверке.** У этой страницы пока нет проверенного человеком перевода на русский, поэтому показан английский оригинал.

# Support policy

::: danger
DRAFT — not legal advice, not published. Blocked on owner action O-5 (legal entity name + jurisdiction).
:::

## Channels

- **Discord** — public, for everyone ([invite link — placeholder, publish at launch]). Community-supported, not a contractual
  channel.
- **Email** — for paid plans (Starter, Pro, Scale): **support@terrascale.tech** (draft address; confirmed before launch).
- **Shared channel with response-time targets** — for Scale.

There is no ticketing product, and no phone support.

Response-time targets: [PLACEHOLDER — targets set before launch].

## Status and incidents

The status page at
[status.terrascale.tech](https://status.terrascale.tech) shows the live SLO
per Region — a 99.9% monthly API availability target — measured by an
external probe. Incidents receive a public postmortem within 5 days.

## Self-service

- `terrascale doctor` diagnoses common setup problems from the CLI.
- Every error the API returns carries its fix — typed errors with upgrade
  paths, e.g. `IndexBuilding: retry in 800 ms, or terrascale index wait`.
- The [docs](/ru/) cover pricing, limits, and every API surface.
