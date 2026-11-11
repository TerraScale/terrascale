# TerraScale documentation site


- Public VitePress documentation lives in `docs/`; theme and routing live in `docs/.vitepress/`. Never publish private database names, hosts, accounts, topology, credentials or internal implementation details.
- `data/canonical/` holds shared product data. `examples/` contains executable SDK examples. `create-terrascale-app/` ships a Next.js starter, including its own AGENTS.md.
- Run area commands from `docs-site/`. Install: `npm ci`. Build: `npm run build`. Generated output check: `npm run check`. From the monorepo root: `make docs-build` (installs the locked graph, runs the generated output check and builds the site).
- `npm run canonical:sync` regenerates pricing, LLM and translated documentation. Keep generated files current. `npm run check:links` validates built links.
- Every feature is always on. Do not add feature flags, activation settings, deploy toggles, kill switches or tuning knobs. Keep one correct value in code. CI allowances for unavoidable third-party switch names need a one-line reason. No fakes, demo stores or skip-auth.
- Document automatic public endpoint discovery. Publish the product contract without claiming unshipped runtime behavior or inventing APIs.
- Changes limited to `AGENTS.md`/`CLAUDE.md` run no build in CI.
