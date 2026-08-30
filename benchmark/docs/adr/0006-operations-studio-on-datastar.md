---
status: accepted
date: 2026-08-30
authority: direct workspace-owner instruction
---

# Private Operations Studio on Datastar

## Decision

TerraBase owns one private Operations Studio in
`crates/terrabase-studio/`. It is a Rust application built with Axum,
Datastar semantics, and Basecoat. It opens one engine location through the
Rust library API and never speaks TBEP. `terrabase-go` remains the only
network client permitted to speak TBEP.

The Studio is a single-operator diagnostic tool. It binds only to a loopback
address and has no authentication system. Startup must fail if configuration
selects a non-loopback listener. The Studio is never a public service, tenant
boundary, customer API, or control-plane authority.

The Studio may inspect and drive every bounded database operation the engine
exports, including documents, TQL queries, indexes, replication, Regions,
tiers, usage, backups, restore, deletion, compaction, health, and metrics.
Its pages must state when a view is simulated, in-memory, or limited to one
local engine. It cannot turn a local observation into a production topology or
performance claim.

Document bodies use Monaco Editor in JSON mode. The editor enables syntax
diagnostics, bracket matching, formatting, and a TerraBase-owned dark theme.
The form retains a plain hidden field as the submitted value. If Monaco cannot
load, the page falls back to the plain JSON textarea and remains usable.

Datastar actions stay typed and bounded. Destructive actions return their
typed engine result. Delete, restore, topology, tier, and compaction actions
require the operator to type the exact displayed target before the Studio
submits them. The server validates the confirmation again and never relies on
browser-only checks. The Studio does not add a generic shell, arbitrary HTTP
request editor, raw TBEP frame editor, or direct storage-file mutation route.

## Deployment

The Studio runs separately from production Members. It may open a development
or operator-designated engine location only when that location has one writer.
The production gateway and public platform do not depend on the Studio.

Monaco and its workers load only from versioned assets vendored with the Studio.
The Studio makes no CDN request. Failure to load Monaco cannot block other
Studio operations. The selected version stays explicit in source and changes
through review.

## Required evidence

CI formats, builds, lints, and tests the standalone Studio crate. Tests prove
loopback-only binding, HTML escaping, bounded form input, JSON rejection,
Monaco fallback, Datastar response framing, graceful shutdown, and that no
Studio route becomes a TBEP or raw storage escape hatch.
