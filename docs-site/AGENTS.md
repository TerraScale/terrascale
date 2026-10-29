# TerraScale documentation site


- Public VitePress documentation lives in `docs/`; theme and routing live in `docs/.vitepress/`. Never publish private database names, hosts, accounts, topology, credentials or internal implementation details.
- `data/canonical/` holds shared product data. `examples/` contains executable SDK examples. `create-terrascale-app/` ships a Next.js starter, including its own AGENTS.md.
- Run area commands from `docs-site/`. Install: `npm ci`. Build: `npm run build`. Full tests: `npm test`. Fast from the monorepo root: `make docs-test-fast` (installs the locked graph, builds the site and runs `node --test scripts/*.test.mjs`). Starter tests: `npm test --prefix create-terrascale-app` after installing its dependencies.
- `npm run canonical:sync` regenerates pricing, LLM and translated documentation. Keep generated files current. `npm run check:links` validates built links.
- Every feature is always on. Do not add feature flags, activation settings, deploy toggles, kill switches or tuning knobs. Keep one correct value in code. CI allowances for unavoidable third-party switch names need a one-line reason. Fakes, demo stores and skip-auth belong only in tests and test binaries.
- Document automatic public endpoint discovery. Publish the product contract without claiming unshipped runtime behavior or inventing APIs.
