# ts-atlas

Read the root [AGENTS.md](../AGENTS.md).

TypeScript Cloudflare Worker with Vite Plus and the Cloudflare Vite plugin; JSON HTTP API only.

## Layout and checks

- `src/contract.ts`: validates complete publishes; `publish.ts`: authenticates them; `store.ts`: single Durable Object writer; `read.ts`: reads memory, KV and D1; `pin.ts`: signs session pins; `geo.ts`: ranks distances; `worker.ts`: serves routes.
- Install: `npm ci`. Type check: `npm run check`. Lint: `npm run lint`. Build: `npm run build`. Local server: `npm run dev`.

## Rules

- D1 (`ATLAS_DB`) is the record, one `AtlasStore` instance (`idFromName("atlas")`) serializes all publishes, and KV (`ATLAS_KV`) caches reads. This deliberate availability exception must work during a TerraBase outage; never add a TerraBase dependency.
- Placements belong to `(project, database)`, with no project default. A null region is a versioned tombstone. Keep monotonicity, whole-request validation and publisher key separation intact.
- Read through 5-second isolate memory, then bounded KV, then D1. `fresh=1` bypasses both caches. Never interpret a failed authoritative read as an absent placement.
- Publish and pin keys stay in bindings and secrets. Never log key material or commit `.dev.vars`.
- `DEV_ORIGINS` is solely the explicit loopback URL validation exception.
- Prove HTTP behavior against the local Worker; never add deploy automation or deploy as part of development.
