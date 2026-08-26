# TerraBaseV2 API host (C#)

C#/.NET host family per the workspace stack policy (charter
PD-2026-08-26-01/02; ADR 0005). Speaks the TerraBase Engine Protocol
(TBEP) to the Rust engine process: typed operations, event-backbone
subscription endpoints, and terrascale.sync.v2 client sessions. Uses the
workspace-pinned .NET SDK; `System.Text.Json` only for JSON surfaces.

Skeleton metadata only — implementation is tracked in
TerraScale/TerraBaseV2#11 (SE-08).
