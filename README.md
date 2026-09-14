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

The site is verified and previewed by `ts-deploy` through this repository's
Forgejo Actions workflow. Every run of the central reusable workflow
(`TerraScale/ts-deploy/.forgejo/workflows/deploy.yml@main`) checks out this
repository at the exact event revision and runs
`ts-deploy ci --repo ts-landing`, which owns the repository's native
verification set: `npm ci`, `npm run check`, `npm run build`, and `npm test`.
There is no Makefile in that path.

- Pages project: `ts-landing` (its `pages.dev` label is `terrascale-landing-page`)
- Production branch: `main`
- Build output: `build/`
- Pages client: the Wrangler pinned in `package-lock.json`

A pull request additionally publishes one Cloudflare Pages preview deployment on
the pull request head branch and posts its URL as a single
`<!-- ts-deploy-preview -->` comment, updated in place on every push. The
non-production branch label is what makes the deployment a preview: it routes no
production traffic and leaves nothing to clean up when the pull request closes.

There is no separate deploy command to remember and no Cloudflare credential in
this repository. The reusable workflow binds `CLOUDFLARE_API_TOKEN` and
`CLOUDFLARE_ACCOUNT_ID` from Forgejo Actions secrets and resolves them only when
the preview runs; a missing value fails closed before Wrangler starts. A
`wrangler.toml` in this working tree pins the same Pages project name for the
local preview, and plays no part in the pipeline.

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

### CI credentials

The deployment workflow receives exactly two Cloudflare Actions secrets,
`CLOUDFLARE_API_TOKEN` and `CLOUDFLARE_ACCOUNT_ID`. No other credential, and no
Cloudflare credential in any other repository, is needed to publish this site.

## Icons

- Add or update icon entries in `scripts/icons-manifest.mjs`
- Run `npm run icons:sync`
- Render icons through `src/components/Icon.svelte`
- Do not import runtime icon packages directly into components

## Assets

Static assets live in `static/` and are served from root-relative URLs such as `/logo-principal.svg` or `/icons/lucide/menu.svg`.

<!-- preview lane test 1789225418 -->
<!-- second push 1789225604 -->
