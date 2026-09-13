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

The site is deployed by `ts-deploy` from this repository's Forgejo Actions
workflow. A push to `develop` runs `./ts-deploy ci --repo ts-landing`, which
verifies the checkout with the native npm check, builds the site, signs one immutable
candidate archive, publishes it to the Forgejo release, and only then deploys
the extracted, byte-verified candidate to Cloudflare Pages.

- Pages project: `terrascale-landing-page`
- Production branch: `main`
- Build output: `build/`
- Pages client: the Wrangler pinned in `package-lock.json`

There is no separate deploy command to remember and no deployment credential in
this repository. The workflow binds `CLOUDFLARE_API_TOKEN` and
`CLOUDFLARE_ACCOUNT_ID` from Forgejo Actions secrets, and `ts-deploy` resolves
them at deploy time only; a missing value fails closed before Wrangler starts.
The project name, production branch, and output directory live in
`ts-deploy`'s site kind, which is the single source of truth for the
destination. A `wrangler.toml` in this working tree is a local development
convenience only and plays no part in deployment.

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
