---
title: Zero-configuration contract
description: Always-on platform behavior, customer authorization and automatic secret delivery.
---

<!-- i18n:fallback:fr -->
> **Traduction en attente.** Cette page n’a pas encore été relue en français et affiche le contenu original en anglais.

<div lang="en" dir="ltr">

# Zero-configuration contract

TerraScale's operating contract is that platform features work without activation flags, deployment toggles or operator tuning.

## Features and regions are always on

Every feature is always available within its authorization and service contract. Every region begins serving when it is ready, without a customer enable step. Availability does not bypass authentication, resource scope, residency or safety limits.

Customer-selected database placement determines where data may reside. Automatic platform operation does not widen that boundary.

## Production secrets arrive automatically

Production secrets come from the platform's secret store. Services receive their credentials automatically; secrets are not copied by hand, committed to this repository or delivered through browser assets. Vendor-issued keys use the same secret-store process.

Customer applications still need authorized credentials. Store those in the application's secret manager and follow [authentication](/fr/guides/authentication).

## Correct defaults belong in code

The platform uses one correct setting for each behavior. Features do not depend on opt-in flags, demo stores or skip-auth production modes. Test fakes remain in test code.

The SDKs take one explicit endpoint, `https://api.terrascale.tech`, or a resolver you supply.

</div>
