# TerraScale documentation site

Read the root [AGENTS.md](../AGENTS.md).

## Layout and checks

- Public VitePress documentation lives in `docs/`; theme and routing live in `docs/.vitepress/`.
- `data/canonical/` holds shared product data; `examples/` contains executable SDK examples; `create-terrascale-app/` ships a Next.js starter (with its own starter `AGENTS.md`).
- Run area commands from `docs-site/`: install `npm ci`, build `npm run build`, generated output check `npm run check` (also compares `data/canonical/` with `https://terrascale.tech`).
- From the monorepo root: `make docs-build` (the CI `docs` leg: compares `data/canonical/` with the same commit's `landing-site/static/{pricing,regions}.json`, installs locked, checks generated output offline and builds the site).
- `npm run canonical:sync` regenerates pricing, LLM and translated docs; keep generated files current. `npm run check:links` validates built links.
- Changes limited to `AGENTS.md` run no build in CI.

## Rules

- Never publish private database names, hosts, accounts, topology, credentials or internal implementation details.
- Document automatic public endpoint discovery. Publish the product contract without claiming unshipped runtime behavior or inventing APIs.
