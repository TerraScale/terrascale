# Starter app (from `create-terrascale-app`)

Next.js + `@terrascale/sdk` with a first live query. There is nothing to
install locally and no emulator: `terrascale dev` gives you a personal cloud
branch of this project's database.

## Run it (under 5 minutes, D-74)

```bash
npm install
terrascale dev --seed ./seed.ts
npm run dev
```

Open the app in two tabs. Write in one tab; the other updates without a
refresh.

## Files

- `app/page.tsx` — first live query (`.subscribe()` over CDN-backed HTTP).
- `terrascale.schema.json` — the contract `terrascale push` applies.
- `seed.ts` — seed for `terrascale dev --seed ./seed.ts`.
- `AGENTS.md` — SDK, schema/SQL builders, `terrascale dev`, MCP setup.

Full quickstart: https://docs.terrascale.tech/getting-started/
