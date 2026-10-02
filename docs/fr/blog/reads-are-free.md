---
---
<!-- i18n:fallback:fr -->
> **Traduction en attente.** Cette page n’a pas encore été relue en français et affiche le contenu original en anglais.

# Reads are free (draft)

> Draft. The final price sheet requires measured economics and owner approval.

::: warning Superseded
This draft predates the Public Alpha catalog. Reads are no longer free:
reads and writes both consume operation units (each started 4 KiB read or
written; writes count once per selected region, reads only in the serving
region), and plans use hard allowances with no paid overages. See
[pricing & limits](/fr/pricing/) for the current model.
:::

Writes and logical storage are the only billed dimensions. Reads, queries,
egress, reactive delivery, subscribers, replay, TTL expiry, and webhooks are
unbilled within disclosed abuse and safety guards. Reaching a guard may
restrict an operation, but never creates an extra charge.

A write’s price is independent of the number of subscribers or shared views
it reaches. Its 4 KiB units are multiplied by selected Regions before
consuming the organization’s included allowance. Databases and branches share
that allowance and its billing cycle. Storage includes retained document
history, measured over time across Regions; backups and indexes are unbilled.

A verified card alone never enables overage. Explicitly approve paid excess
after reviewing rates and spending controls. See the [pricing policy](/fr/pricing/)
for allowance exhaustion, continuing storage costs, and deletion notices.

Unbilled delivery is a billing rule, not a claim of unlimited measured
capacity. Performance and million-reader claims require separate evidence.
