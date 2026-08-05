# TerraScale Landing Page — Agent Notes

Operational guide for AI agents working in this repo. Read before making changes.

## Public URL

The production public site is **https://terrascale.tech**.

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
- `/about` → `src/pages/About.svelte`
- `/404` → `src/pages/NotFound.svelte`

`/about` is published. `/terms` stays unpublished until approved legal text exists. Do not restore `/security` or `/terms` without explicit approval.

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

The sibling Documentation site intentionally uses a Quantico-only, self-hosted font policy. This divergence is intentional; do not make the two sites match.

Note: the sibling TerraScale.Documentation site intentionally uses a different (Quantico-only, no external CDN) font policy — this divergence is by design, so do not "fix" this site to match it.

### Tokens & Utilities

Design tokens live on `:root` in `src/index.css`. Reach for the shared utility classes before writing new bespoke CSS. Scoped `<style>` blocks are fine for section-specific layout but should still compose on top of the tokens.

### Conflict Resolution

When the handoff README disagrees with the reference JSX files under `docs/design_handoff_terrascale_landing/design-reference/`, the JSX is historical design reference for layout only; product claims must match live code and docs.

## Icons

- Source icons from Iconify, but do not render them through an npm runtime or build plugin in the app.
- Checked-in individual SVG files live in `static/icons/` (served at `/icons/...`). Do not use sprite sheets.
- Render every icon through `src/components/Icon.svelte` and the registry in `src/lib/icons.ts`. Do not import icon packages directly in components.
- When adding or changing icons, update `scripts/icons-manifest.mjs` and run `npm run icons:sync`.
- The writer changing the icon manifest exclusively owns the resulting `static/icons/` sync output; do not regenerate or edit another writer's generated icon output. Apply the same ownership rule to content-derived generated artifacts.
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
npm run cf:project:create # retired: always refuses external execution
npm run cf:pages:dev     # build + wrangler pages dev
npm run cf:deploy        # retired: always refuses external execution
npm run cf:deploy:preview # retired: always refuses external execution
npm run cf:pages:verify  # optional non-authorizing Pages Read diagnostic
```

Before handing work back, verify with: `npm run check && npm run lint && npm run build`.

Workspace root `/workspaces/TerraScale/AGENTS.md` Git policy applies: pull latest `develop` before work, implement on `develop`, **commit often** on substantial coherent changes, and **push directly to `origin/develop`**. Do not open pull requests for day-to-day work. Do not create feature branches or worktrees unless the user explicitly asks. Pushes to `develop` run no Forgejo automation. Remote `main` is the production deployment/admission target only; it does not authorize source changes or routine delivery outside `develop`, nor bypass the required `develop` → `main` promotion/admission. Only that promotion path may build the immutable site candidate.

## Deployment

- Target: future Cloudflare Pages Git integration (Direct Upload retired)
- Build command: `npm run build`
- Output directory: `build/`
- CLI: Wrangler (`npm run cf:*`)
- Pages project config lives in `wrangler.toml` (source of truth for project name, compatibility date, bindings)

Normal CI must not receive `CLOUDFLARE_API_TOKEN` or any Pages Write credential. `cf:pages:verify` is the only retained remote command and uses a separate `CLOUDFLARE_PAGES_READ_TOKEN` for non-authorizing metadata diagnostics.

### Deployment admission

- Routine local development and verification commands (`npm run dev`, `npm run check`, `npm run lint`, `npm run build`, and `npm run preview`) are safe to run without deployment authorization; they must not create projects, retarget deployments, or alter external production state.
- `cf:project:create`, `cf:deploy`, and `cf:deploy:preview` are permanently fail-safe deprecated: they must refuse before fetch, Wrangler, or any external mutation because Direct Upload requires Pages Write and cannot be constrained to deploy-only. Do not add a Pages Write token to CI or revive these commands.
- The safe future delivery design is a new Git-integrated Pages project backed by a protected branch in a supported hosted GitHub or GitLab mirror. Forgejo direct integration is unsupported. This workspace grants no authority to create, migrate, retarget, or deploy that project.

## Conventions

- Prefer editing existing components/utilities over introducing new ones.
- Public TypeScript examples must use the ordinary customer `@terrascale/sdk` contract. Do not show Dashboard-only client identifiers, headers, DTOs, routes, tenant bypasses, database identifiers, direct TerraBase access, or private APIs.
- Keep `src/lib/site.ts` as the single source of truth for nav, footer, and social link data.
- Use `RouteLink` for internal routes and `ExternalLink` for external URLs — don't hand-roll `<a>` tags for navigation.
- Use the `reveal` action (CSS classes `ts-reveal`, `ts-reveal-up`, `is-visible`) for scroll-in animation instead of rolling new IntersectionObservers.
- Keep scoped component styles scoped; promote shared patterns to `src/index.css` utilities when they recur.
- Do not create markdown or README files unless explicitly requested.
- Never add an AI agent (Claude, Copilot, GPT, etc.) as a co-author on commits. No `Co-Authored-By:` trailers for AI tools, no agent-generated signatures in commit messages, no "Generated with <tool>" footers.

## Concurrent Changes and Handoff

- Before editing, staging, or committing, check `git status --short`, agree named-path ownership, and preserve other writers' changes. Generated output belongs exclusively to the writer who owns its source input.
- Never place secrets or credentials in source, documentation, generated output, or staging. Redact them from tests, validation output, and handoff reports.
- Stage named owned files only. Before pushing, fetch `origin` and stop if `develop` moved.
- Before handoff, report final `git status --short`, the owned diff, the final fetch result, files, checks, and commit.
