---
---
<!-- i18n:fallback:pt-br -->
> **Tradução pendente.** Esta página ainda não passou por revisão humana em português e mostra o conteúdo original em inglês.

# Pricing philosophy

**Two meters: operation units and storage across regions.** Every plan
includes a monthly operation-unit allowance and a storage allowance. During
Public Alpha those allowances are hard limits: there are no paid overages,
and reaching one never creates a charge (see [pricing & limits](/pt-br/pricing/)).

Stated precisely, because honesty is the brand:

- **Infrastructure is not free.** We pay for the edge, the regions, and the
  hardware. The meters follow the work you ask for: the bytes you read and
  write, and the bytes you keep.
- **Replicated writes count once per region.** We never say "no per-region
  cost". Multi-region durability is real work, and it is metered as exactly
  what it is, shown up front when you select regions.

## The operation unit

One operation unit is each started 4 KiB (4,096 bytes) of a logical object
successfully read or written. Lists, queries, and batches count each item
independently. Writes count once per selected region; reads count only in the
serving region. Failed, denied, invalid, throttled, and not-found requests are
not billed. That is the entire metering rule.

A write counts **once per selected region** regardless of how many shared
views it affects or how many clients are attached to them.
Authorization-equivalent subscribers share one view, one delta computation,
and one log append, so fan-out does not multiply the write.

## The gigabyte

Stored bytes count once in every selected region: the canonical bytes of your
current documents plus retained history, TTL-aware. Indexes, backups, and
service-managed storage overhead do not count toward storage.

## Prices require evidence

Each plan price must be backed by measured unit costs, and guarded dimensions
must have measured worst cases under their guards. Customer usage facts must
reconcile with invoices.

A stored card does not authorize charges beyond your plan. Databases and
branches share normal organization allowances; there is no additional branch
allowance. Review the [billing policy](/pt-br/pricing/) for allowance exhaustion,
continuing storage costs, and retention policies.
