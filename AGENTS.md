# ts-landing

Follow the workspace [Shipping code](../AGENTS.md#shipping-code-user-2026-10-29-go-fast-break-things-fast-q-trunk-q-local-check) rules in `/home/mariogk/Projects/TerraScale/AGENTS.md`.

- Public site: `https://terrascale.tech`. Use SvelteKit, Svelte, TypeScript, Vite and the shared Tailwind `@theme` tokens in `src/index.css`.
- Routes live in `src/routes/`; reusable components in `src/components/`; static assets in `static/`; build and validation helpers in `scripts/`.
- Install: `npm ci`. Build and static validation: `npm run build`. Fast: `node --test scripts/*.test.mjs`. Asset refresh: `npm run icons:sync` and `npm run world-map:sync`.
- Ship static HTML, CSS, JavaScript and assets from `build/` using `@sveltejs/adapter-static`. Prerender every public page from the root layout. Keep SSR enabled at build time, strict prerender checks and no SPA fallback.
- No runtime SSR, application server, Workers, Pages Functions, server actions or request-time API handlers. Fully generated build-time resources are fine. A runtime server requires human approval and an updated deployment plan.
- Keep browser-only code out of build-time execution. Never include private credentials in bundles or generated files.
- Use current stable compatible dependencies; check installed-version docs and migration requirements before upgrades. Keep manifests and lockfile in sync; use existing scripts and avoid competing build pipelines or frameworks.
- Reuse small Svelte components, shared theme tokens and committed brand assets. Preserve branding, typography, spacing and interactions unless assigned to change them.
- Support mobile and desktop without horizontal overflow; respect reduced motion. Use semantic HTML, keyboard-accessible controls, visible focus and image alternatives.
- Keep primary content and navigation in prerendered HTML. Preserve titles, descriptions, social metadata and canonical URLs under `https://terrascale.tech`.
- Optimize assets, defer nonessential browser code and avoid unnecessary dependencies or blocking third-party scripts.
- Cover navigation, calls to action, forms and important interactive states. Check direct navigation and refresh, responsive layouts, links, assets, hydration and console errors. Validate generated output, not just the dev server.
- Every feature is always on. Do not add feature flags, activation settings, deploy toggles, kill switches or tuning knobs. Keep one correct value in code. CI allowances for unavoidable third-party switch names need a one-line reason. Fakes, demo stores and skip-auth belong only in tests and test binaries.
- Do not invent product claims, APIs or URLs, or bypass authorization, tenant scope, registered callbacks, payment consent or safety guards. The contract requires automatic public endpoint discovery; README records SDK and qualification gaps and existing switches remain owning implementation work.
