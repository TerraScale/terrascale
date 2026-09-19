# TerraScale architecture atlas

Target request flow and infrastructure, reconciled with source on 2026-09-07. Start with two
member servers per Region, then add capacity from observed demand.

| Regions | Starting servers | Overview | Mermaid detail |
| --- | ---: | --- | --- |
| 3 | 6 | [PNG](architecture-3-regions.png) · [SVG](architecture-3-regions.svg) | [Source](architecture-3-regions.mmd) |
| 10 | 20 | [PNG](architecture-10-regions.png) · [SVG](architecture-10-regions.svg) | [Source](architecture-10-regions.mmd) |
| 20 | 40 | [PNG](architecture-20-regions.png) · [SVG](architecture-20-regions.svg) | [Source](architecture-20-regions.mmd) |

## Reading the pictures

REST first contact discovers ranked, eligible regional endpoints through the
ts-atlas metadata Worker on atlas.terrascale.tech, then goes directly to the
regional origin; failed or overloaded pins must re-enter discovery. The target
excludes draining members, bounds safe retries, and preserves sessions and
idempotency.
A regional SQLite database still has one writer. Losing one of two hosts cannot
preserve RF=2 until a replacement catches up; balancing does not waive durability.

Reactive snapshots and logs pass through the capability Snippet and CDN, with
no Worker on that hot path. SQLite WAL is replicated physically inside a Region;
causal journals travel directly over QUIC between placed Regions. The dashboard
runs on Cloudflare Workers and owns Better Auth/Stripe product flows; its public
SDK reads the control-plane database like a customer application.

OCI hosts run native binaries under systemd, without a container runtime.
Every member host also runs a local orchestrator, ts-node, and Grafana agent.
GRU holds fenced global mutation authority. The embedded global control store
stays at three replicas in GRU/SFO/FRA for every estate size. Additional Regions
are location placeholders and do not automatically expand database residency.
A separate regional R3 needs at least three eligible hosts and explicit admission.

Only writes and logical storage are billed. Both count selected Regions before
consuming shared organization allowances; logical storage includes retained
history and is measured over time. Reads, reactive delivery, egress and backup
storage are unbilled. A safety guard never creates an additional charge.
Placement is immutable across replication, backups, restore, exports and branches.
Backup policy may select OCI Object Storage, Tigris or both; admission must prove
each destination satisfies the database's permitted geography. Encryption alone
does not establish residency.

These are design pictures, not deployment or performance evidence. Three Regions
are the launch target; ten and twenty are future capacity designs. A1 Flex
2 OCPU / 12 GB is the initial host target, with qualified ARM/x86 fallback and
scaling from measured demand. Two hosts are the initial footprint, not a capacity
claim. Loss of a follower prevents new RF=2 acknowledgements until durability is
restored. Admitted data service during control outages still depends on meeting
authorization, placement, session and requested durability requirements.

## Source reconciliation

- [Orchestrator architecture](../../../ts-orchestrator/docs/guides/architecture-boundaries.md):
  local orchestration on every host, ts-node host mutation, fenced GRU authority,
  permanent GRU/SFO/FRA global R3 and native systemd lifecycle.
- [Replication decision](../../../terrabase/docs/adr/0044-sync-and-replication-engines.md)
  and [GlobalDurable admission](../../../terrabase/docs/adr/0035-one-region-global-durable-semantics.md):
  local physical RF=2 plus cross-Region causal dependencies; GlobalDurable needs
  at least two active selected Regions and durability in every selected Region.
- [Backup implementation boundary](../../../terrabase/src/TerraBase/Backup/BackupManager.cs):
  native backup target, approved destinations and independent restore/residency
  qualification. Replacement and qualification gates remain authoritative.
- [Discovery entrypoint](../../../ts-atlas/src/index.ts) and
  [reactive Snippet](../../../ts-atlas/snippet/snippet.ts): metadata discovery
  is separate from capability-gated Worker-free reactive delivery.
- [Pricing source](../../data/pricing.json): draft rates, organization allowances,
  Region multiplication, consent and continuing storage charges.

Historical storage and benchmark drafts were removed. Repository-owned ADRs,
source, and qualification records control implementation and evidence.
This atlas remains outside public product navigation and VitePress build inputs.

## Editable sources and rendering

`draw.mjs` generates the overview SVGs with a deliberate layout. `render.mjs`
uses Playwright/Chromium to capture those SVGs as PNGs. The `.mmd` files are
separate, expanded Mermaid detail views; they do not generate the overview SVGs.
Render both forms after changing labels; SVG/PNG overviews and Mermaid detail
views are independently editable, so verify their policies remain consistent.

From this directory, with Node and Playwright available:

```sh
node draw.mjs
node render.mjs
npx --package @mermaid-js/mermaid-cli@11.12.0 mmdc -i architecture-3-regions.mmd -o /tmp/terrascale-detail-3.svg -c mermaid.config.json
```

Repeat the Mermaid command for 10 and 20. `CHROME_BIN` selects an installed
Chromium for the overview renderer. `PLAYWRIGHT_MODULE` can select an absolute
module URL. On hosts without browser libraries, provide the platform library
path; Mermaid CLI also supports `-p` with a local Puppeteer executable config.
