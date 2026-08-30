# TerraScale.DotNet ADR index

These records define the public .NET SDK boundary and package-release rules.
The workspace [`../../../GOAL.md`](../../../GOAL.md) and repository
[`../../GOAL.md`](../../GOAL.md) control the product target. An accepted SDK
boundary is not live service or production evidence.

| ADR | Status | Decision |
| --- | --- | --- |
| [0001](0001-support-dotnet-10-lts.md) | Accepted; qualification pending | Support the pinned .NET 10 LTS target and verify the exact package. |
| [0002](0002-reactive-v2-client-boundary.md) | Accepted boundary; qualification pending | Keep result-view semantics separate from transport until the public contract exists. |
| [0003](0003-package-provenance-gates.md) | Accepted workflow boundary; qualification pending | Promote only an immutable package whose source and tree verify in a clean consumer. |
| [0004](0004-public-contract-parity-and-compatibility.md) | Accepted boundary; qualification pending | Every operation follows an owner-published versioned public contract. |
| [0005](0005-reactive-conformance-and-transition.md) | Accepted boundary; qualification pending | Replace legacy reactive behavior only after shared vectors and live recovery pass. |
