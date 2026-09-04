# TerraScale.DotNet ADR index

These records define the public .NET SDK boundary. The workspace
[`../../../GOAL.md`](../../../GOAL.md) and repository [`../../GOAL.md`](../../GOAL.md)
control the product target. An accepted SDK boundary is not live service or
production evidence.

| ADR | Status | Decision |
| --- | --- | --- |
| [0001](0001-support-dotnet-10-lts.md) | Accepted; qualification pending | Support the pinned .NET 10 LTS target and verify the exact package set. |
| [0002](0002-reactive-v1-client-boundary.md) | Accepted boundary; qualification pending | Keep result-view semantics separate from transport until the public contract exists. |
| [0004](0004-public-contract-parity-and-compatibility.md) | Accepted boundary | Every operation follows an owner-published versioned public contract; the pinned contract is the mechanism. |
