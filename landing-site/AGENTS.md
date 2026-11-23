# ts-landing

Read the root [AGENTS.md](../AGENTS.md).

Public site at `https://terrascale.tech`, built with SvelteKit, Svelte, TypeScript, Vite and the shared Tailwind `@theme` tokens in `src/index.css`.

## Layout and checks

- Routes: `src/routes/`; reusable components: `src/components/`; static assets: `static/` (owns canonical `static/{pricing,regions}.json`); build/validation helpers: `scripts/`.
- Install: `npm ci`. Build and static validation: `npm run build`. Asset refresh: `npm run icons:sync` and `npm run world-map:sync`. From the monorepo root: `make landing-build`.
- Changes limited to `AGENTS.md` run no build in CI.

## Rules

- Ship static HTML, CSS, JavaScript and assets from `build/` using `@sveltejs/adapter-static`. Prerender every public page from the root layout with build-time SSR enabled, strict prerender checks and no SPA fallback.
- No runtime SSR, application server, Workers, Pages Functions, server actions or request-time API handlers (a runtime server requires human approval and an updated deployment plan). Fully generated build-time resources are fine.
- Keep browser-only code out of build-time execution. Never include private credentials in bundles or generated files.
- Reuse small Svelte components, shared theme tokens and committed brand assets. Preserve branding, typography, spacing and interactions unless assigned to change them.
- Support mobile and desktop without horizontal overflow; respect reduced motion. Use semantic HTML, keyboard-accessible controls, visible focus and image alternatives.
- Keep primary content and navigation in prerendered HTML. Preserve titles, descriptions, social metadata and canonical URLs under `https://terrascale.tech`.
- Optimize assets, defer nonessential browser code and avoid unnecessary dependencies or blocking third-party scripts.
- Validate generated output (not just the dev server): navigation, CTAs, forms, interactive states, direct navigation/refresh, responsive layouts, links, assets, hydration and console errors.
- Do not invent product claims, APIs or URLs. Document automatic public endpoint discovery; `README.md` records SDK and qualification gaps.
