---
status: accepted
date: 2026-08-29
authority: ../../../GOAL.md
---

# One-Region `GlobalDurable` semantics

## Context

ADR 0033 defines `GlobalDurable` as the level that reaches every selected
active Region and limits its complete-Region-loss RPO 0 promise to databases
with at least two selected Regions. The current cluster durability lane
records a `GlobalDurable` receipt for a one-Region shape: both Members of that
Region contribute, but complete loss of the only Region still loses the
acknowledged write. The lane calls this an explicit degenerate behavior and
does not claim production qualification.

This distinction affects the customer-visible durability contract, admission
validation, error handling, documentation, and compatibility. It is resolved
by this accepted fail-closed decision rather than left implicit in a test or
receipt.

## Options

### Option A — retain the explicit degenerate level

Permit `GlobalDurable` in a one-Region database when every selected Member has
stored the write. Document that the level means every selected Region and that
one selected Region provides no Region-loss protection. Keep the complete-loss
consequence visible in the receipt and prevent any RPO 0 wording for this
shape.

### Option B — refuse the level for one Region

Return a typed refusal before acknowledgement when fewer than two active
Regions are selected. Require `RegionalDurable` or `BackupDurable` according to
the product contract. Update the cluster durability implementation, vectors,
gateway mapping, and tests together so no caller can interpret a local
`GlobalDurable` receipt as Region-loss protection.

## Decision

Choose **Option B — refuse the level for one Region**. `GlobalDurable` must
return a typed refusal before acknowledgement when fewer than two active
Regions are selected. A one-Region database may request `RegionalDurable` or
`BackupDurable` according to its configured contract, but no acknowledgement
for that shape may imply protection from complete loss of its only Region.

Option A is rejected for production: an acknowledgement that names
`GlobalDurable` while the only selected Region can be destroyed would be
misleading even when every selected Member has stored the write.

This is a fail-closed product contract. The current cluster implementation's
degenerate one-Region `GlobalDurable` receipt is therefore a known
implementation incompatibility. Until the refusal is wired, the current
behavior must not be exposed as a production durability guarantee, SLO, or
marketing claim.

## Implementation and conformance work

The accepted contract requires a focused implementation and test update for
one, two, and three selected Regions; a typed public contract response; a
gateway/SDK conformance vector; and an updated qualification receipt. Release
evidence must also show that Region-loss, backup, retry, and restore
documentation use the same meaning and that no downgrade or silent durability
weakening occurs. These are tracked implementation and qualification gaps,
not unresolved owner choices.

Until that evidence exists, the repository status remains pre-production even
though this ADR's semantic decision is accepted.
