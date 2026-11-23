# ts-atlas

- TypeScript Cloudflare Worker with Vite Plus and the Cloudflare Vite plugin; JSON HTTP API only.
- `src/contract.ts` validates complete publishes; `publish.ts` authenticates them; `store.ts` contains the single Durable Object writer; `read.ts` reads memory, KV and D1; `pin.ts` signs session pins; `geo.ts` ranks distances; `worker.ts` serves routes.
- Install: `npm ci`. Type check: `npm run check`. Lint: `npm run lint`. Build: `npm run build`. Local server: `npm run dev`.
- D1 (`ATLAS_DB`) is the record, one `AtlasStore` instance (`idFromName("atlas")`) serializes all publishes, and KV (`ATLAS_KV`) caches reads. This deliberate availability exception must work during a TerraBase outage. Never add a TerraBase dependency.
- Placements belong to `(project, database)`, with no project default. A null region is a versioned tombstone. Keep monotonicity, whole-request validation and publisher key separation intact.
- Read through 5-second isolate memory, then bounded KV, then D1. `fresh=1` bypasses both caches. Never interpret a failed authoritative read as an absent placement.
- Publish and pin keys stay in bindings and secrets. Never log key material or commit `.dev.vars`.
- No comments in new code. No tests outside the journey; prove HTTP behavior against the local Worker. Never add deploy automation or deploy as part of development.
- Every feature is always on. Do not add activation flags, switches, kill switches or legacy routes. `DEV_ORIGINS` is solely the explicit loopback URL validation exception.
