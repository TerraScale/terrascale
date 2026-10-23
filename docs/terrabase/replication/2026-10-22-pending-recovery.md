# Conservative recovery with collected history

A copying refill cannot use source absence and an age bound as deletion evidence
for a live local state. Ordinary refill refuses that comparison before forgetting
any candidate; it leaves local state available for recovery and the stream cut in
place.

Certified rebuild also refuses source-gap reconciliation that would remove live
state, or side-copy salvage that would filter a live state through the collected
frontier. Refusal leaves the rebuild uncertified and retains the original side
files. A retry may resume when an intact source provides enough state to complete
without those ambiguities.

The strict join floor remains in force. Recovery availability is deliberately
conservative: automatic resolution of an ambiguous history requires durable causal
or deletion evidence, which is deferred to a separate change. This PR adds no
such metadata and does not change the storage version.

Wire additions remain optional. Coverage rollback detection protects upgraded
peer pairs; a certified source must report its collected frontier before it can
provide a certified baseline.
