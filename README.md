# TerraScale Landing Page

The official TerraScale marketing site, built as a fully prerendered SvelteKit app for static deployment on Cloudflare Pages.

## Stack

- SvelteKit with Svelte 5 and TypeScript
- Vite 8
- Tailwind CSS 4
- Static prerendering via `@sveltejs/adapter-static`
- Local SVG icon registry sourced from Iconify

## Development

```bash
npm install
npm run dev
```

The app is routed with SvelteKit file-based routes and prerenders these public pages:

- `/` -> `src/pages/Home.svelte`
- `/about` -> `src/pages/About.svelte`
- `/pricing` -> `src/pages/Pricing.svelte`
- `/contact` -> `src/pages/Contact.svelte`
- `/404` -> `src/pages/NotFound.svelte`

## Scripts

```bash
npm run dev
npm run build
npm run preview
npm run check
npm run lint
npm run icons:sync
npm run cf:pages:dev
```

## Deployment

This repository's Forgejo Actions workflow (`.forgejo/workflows/ci.yml`) is
verification only. A push to `main` — and every pull request targeting
`main` — checks out the event revision, installs the locked dependencies
with `npm ci`, and runs `npm run check` and `npm run lint`.

Nothing in this repository deploys the site: the workflow holds no
Cloudflare credential, publishes no release archive, and creates no
per-pull-request preview deployment. Publishing the built site to the
Cloudflare Pages project `ts-landing` (build output `build/`, Pages client
the Wrangler pinned in `package-lock.json`) is performed outside this
repository's workflow. The `wrangler.toml` in this working tree pins the
Pages project name for the local preview and plays no part in any pipeline.

This repo is intentionally configured for static output only. Do not assume
Cloudflare Functions, Workers, or SSR are part of the deployment flow unless the
repo instructions change.

### Local Pages Preview

To verify the static build through the Cloudflare Pages CLI locally:

```bash
npm run cf:pages:dev
```

That builds the site and serves the generated `build/` output through
`wrangler pages dev`.

## Icons

- Add or update icon entries in `scripts/icons-manifest.mjs`
- Run `npm run icons:sync`
- Render icons through `src/components/Icon.svelte`
- Do not import runtime icon packages directly into components

## Assets

Static assets live in `static/` and are served from root-relative URLs such as `/logo-principal.svg` or `/icons/lucide/menu.svg`.
