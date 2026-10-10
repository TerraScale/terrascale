---
title: How TerraScale runs with zero configuration
description: Always-on regions, automatic node discovery and automatic production credentials.
---

<!-- i18n:fallback:de -->
> **Übersetzung ausstehend.** Diese Seite wurde noch nicht auf Deutsch geprüft und zeigt den englischen Originalinhalt.

# How TerraScale runs with zero configuration

TerraScale's operating contract is that the platform works without feature
activation, deployment switches or tuning settings. Every feature is always
on. Infrastructure work still in progress is recorded in the
[zero-configuration inventory](/de/zero-config-inventory); this contract does not
claim that every historical switch has already been removed.

## Every region serves automatically

Every region runs all the time. A region does not need an enable step before
it can serve requests. The platform discovers the available regions and a
node learns its own region from where it runs. Clients discover regional
endpoints automatically.

Database placement still defines where a customer's data may live. Automatic
region discovery never widens that residency boundary. See
[regions and placement](/de/guides/).

## Network membership admits a node

A node on the private NetBird network joins the cluster automatically,
discovers every region and starts serving. Membership in that network is the
admission boundary; there is no separate cluster-join or per-region feature
switch. Existing serving nodes are updated in place.

## Production secrets arrive automatically

OCI Vault creates, delivers and configures first-party production secrets
automatically. Services receive the credentials they need without an operator
copying keys, writing environment files or toggling secret delivery.

Third-party providers such as Stripe and Cloudflare issue their own keys.
Those keys are stored once in OCI Vault and picked up automatically. This
vendor-issued input does not turn a TerraScale feature on or off.

Customer sign-in and application credentials still authorize access to customer
data. Automatic service credentials do not grant anonymous access or put
production secrets in browser bundles.

## Correct behavior belongs in code

Each value has one correct setting in code. Production binaries contain no
feature flags, demo stores, skip-auth modes or disable switches. Fakes and
demo modes live only in test code and test binaries.

Every repository states this rule. A shared CI check rejects newly added
switch-style configuration, and code review requests changes when a new
switch violates the contract. Historical switches are inventoried for removal
in their owning repositories.
