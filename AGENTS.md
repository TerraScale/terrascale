# TerraScale Landing Page — Agent Notes

Operational guide for AI agents working in this repo. Read before making changes.

## Stack

- SvelteKit + Svelte 5 + TypeScript
- Vite 8
- Tailwind CSS 4 (with `@theme` block in `src/index.css`)
- Static prerendering via `@sveltejs/adapter-static`
- No SSR, no server adapters, no Cloudflare Workers/Functions

The entire app ships as static HTML/CSS/JS from `build/`. Do not introduce code paths that require a runtime server unless the repo deployment story is explicitly changed first.

## Routes

Only these routes exist and are prerendered. Do not add or reintroduce others without an explicit request.

- `/` → `src/pages/Home.svelte`
- `/pricing` → `src/pages/Pricing.svelte`
- `/contact` → `src/pages/Contact.svelte`
- `/404` → `src/pages/NotFound.svelte`

Routes previously present (`/about`, `/security`, `/terms`) were removed as part of the design handoff and should not be restored unless requested.

## Repository Layout

- `src/app.html` — document shell, font preconnects, Google Fonts stylesheet
- `src/index.css` — design tokens, base styles, shared utility classes (`.ts-glass`, `.ts-eyebrow`, `.ts-text-gradient`, `.ts-btn-primary`, `.cta-shine`, `.pulse-dot`, `.ts-reveal*`, `.tok-*`)
- `src/lib/site.ts` — navigation, footer columns, social links (single source of truth for chrome copy/links)
- `src/lib/highlight.ts` — minimal per-language tokenizer plus `CODE_SAMPLES` and `LANG_META`
- `src/lib/icons.ts` — icon registry helpers
- `src/lib/motion/` — `reveal` IntersectionObserver action
- `src/components/` — `Header`, `Footer`, `Icon`, `RouteLink`, `ExternalLink`, `ScrollReveal`
- `src/components/sections/` — page sections (Hero, Features, CodePanel, PricingSection, CtaSection, PricingFAQ, DetailedPricingTable, PricingFeatureTriad, PricingCTA, SectionHeader)
- `src/pages/` — top-level page components composed from sections
- `src/routes/` — SvelteKit route files that mount the page components
- `static/` — assets served from root (`/logo-principal.svg`, `/icons/lucide/*.svg`, etc.)
- `scripts/` — Cloudflare Pages + icon sync scripts
- `docs/design_handoff_terrascale_landing/` — original design handoff reference (README + JSX source of truth for layout/copy)

## Design System

The design config is **locked** — do not introduce alternate themes or toggles.

- `data-theme="focused"` (blue primary)
- `data-glass="subtle"`
- `data-atmosphere="dramatic"` (1.9× multiplier)

### Fonts

Three-font stack, all referenced in `src/app.html`:

- **Quantico** — body, self-hosted woff2 in `static/fonts/`
- **Roboto** — eyebrows, Google Fonts CDN
- **Roboto Mono** — code/stats, Google Fonts CDN (fallback: JetBrains Mono)

Note: the sibling Documentation site intentionally uses a different (Quantico-only, no external CDN) font policy — this divergence is by design, so do not "fix" this site to match it.

### Tokens & Utilities

Design tokens live on `:root` in `src/index.css`. Reach for the shared utility classes before writing new bespoke CSS. Scoped `<style>` blocks are fine for section-specific layout but should still compose on top of the tokens.

### Conflict Resolution

When the handoff README disagrees with the reference JSX files under `docs/design_handoff_terrascale_landing/design-reference/`, the JSX is source of truth for copy and layout.

## Icons

- Source icons from Iconify, but do not render them through an npm runtime or build plugin in the app.
- Checked-in individual SVG files live in `static/icons/` (served at `/icons/...`). Do not use sprite sheets.
- Render every icon through `src/components/Icon.svelte` and the registry in `src/lib/icons.ts`. Do not import icon packages directly in components.
- When adding or changing icons, update `scripts/icons-manifest.mjs` and run `npm run icons:sync`.
- Prefer `lucide` for generic UI glyphs and `simple-icons` for brand/social logos.
- Keep SVGs monochrome and `currentColor`-driven so Tailwind text-color classes control appearance.
- Do not add or prefer React, runtime Lucide, or build-time icon packages for new work here.

## Scripts

```bash
npm run dev              # vite dev server
npm run build            # vite build + inline prerendered CSS
npm run preview          # preview built output
npm run check            # svelte-kit sync + svelte-check + tsc for node config
npm run lint             # eslint
npm run icons:sync       # regenerate static/icons/ from scripts/icons-manifest.mjs

npm run cf:login         # wrangler login
npm run cf:whoami        # confirm wrangler identity
npm run cf:project:create
npm run cf:pages:dev     # build + wrangler pages dev
npm run cf:deploy        # production deploy
npm run cf:deploy:preview
```

Before handing work back, verify with: `npm run check && npm run lint && npm run build`.

## Deployment

- Target: Cloudflare Pages static direct upload
- Build command: `npm run build`
- Output directory: `build/`
- CLI: Wrangler (`npm run cf:*`)
- Pages project config lives in `wrangler.toml` (source of truth for project name, compatibility date, bindings)

CI needs `CLOUDFLARE_API_TOKEN` (`Account / Cloudflare Pages / Edit`) and `CLOUDFLARE_ACCOUNT_ID`. See `README.md` for the full non-interactive deploy env var list.

## Conventions

- Prefer editing existing components/utilities over introducing new ones.
- Keep `src/lib/site.ts` as the single source of truth for nav, footer, and social link data.
- Use `RouteLink` for internal routes and `ExternalLink` for external URLs — don't hand-roll `<a>` tags for navigation.
- Use the `reveal` action (CSS classes `ts-reveal`, `ts-reveal-up`, `is-visible`) for scroll-in animation instead of rolling new IntersectionObservers.
- Keep scoped component styles scoped; promote shared patterns to `src/index.css` utilities when they recur.
- Do not create markdown or README files unless explicitly requested.
- Never add an AI agent (Claude, Copilot, GPT, etc.) as a co-author on commits or pull requests. No `Co-Authored-By:` trailers for AI tools, no agent-generated signatures in commit messages, no "Generated with <tool>" footers.
