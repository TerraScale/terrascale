# TerraScale architecture atlas

Target v1 request flow and infrastructure, reviewed 2026-09-04. Start with two
member servers per Region, then add capacity from observed demand.

| Regions | Starting servers | Overview | Mermaid detail |
| --- | ---: | --- | --- |
| 3 | 6 | [PNG](architecture-3-regions.png) · [SVG](architecture-3-regions.svg) | [Source](architecture-3-regions.mmd) |
| 10 | 20 | [PNG](architecture-10-regions.png) · [SVG](architecture-10-regions.svg) | [Source](architecture-10-regions.mmd) |
| 20 | 40 | [PNG](architecture-20-regions.png) · [SVG](architecture-20-regions.svg) | [Source](architecture-20-regions.mmd) |

## Reading the pictures

REST enters the ts-proxy Worker, which must select a healthy member with spare
capacity within database placement. SDKs can pin later REST to a regional
origin; failed or overloaded pins must re-enter selection. The target excludes
draining members, bounds safe retries, and preserves sessions and idempotency.
A regional SQLite database still has one writer. Losing one of two hosts cannot
preserve RF=2 until a replacement catches up; balancing does not waive durability.

Reactive snapshots and logs pass through the capability Snippet and CDN, with
no Worker on that hot path. SQLite WAL is replicated physically inside a Region;
causal journals travel directly over QUIC between placed Regions. The dashboard
runs on Cloudflare Workers and owns Better Auth/Stripe product flows; its public
SDK reads the control-plane database like a customer application.

Every member host also runs a local orchestrator, ts-node, and Grafana agent.
GRU holds fenced global mutation authority. The embedded global control store
stays at three replicas in GRU/SFO/FRA for every estate size. Additional Regions
are location placeholders and do not automatically expand database residency.
A separate regional R3 needs at least three eligible hosts and explicit admission.

These are design pictures, not deployment evidence. Member-aware capacity routing,
production membership, auth composition, and incremental view publication still
have implementation gaps described in the [review](../workspace-2026-09-04.md).
The launch qualification targets three Regions; ten and twenty are scale designs.

## Editable sources and rendering

`draw.mjs` generates the overview SVGs with a deliberate layout. `render.mjs`
uses Playwright/Chromium to capture those SVGs as PNGs. The `.mmd` files are
separate, expanded Mermaid detail views; they do not generate the overview SVGs.
All three overview images were visually inspected. Mermaid CLI 11.12.0 was used
to render and validate the detail views.

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
