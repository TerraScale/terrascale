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
- `/terms` -> `src/pages/Terms.svelte`
- `/404` -> `src/pages/NotFound.svelte`

## Scripts

```bash
npm run dev
npm run build
npm run preview
npm run check
npm run lint
npm run icons:sync
npm run cf:login
npm run cf:project:create
npm run cf:pages:dev
npm run cf:deploy
npm run cf:deploy:preview
```

## Deployment

- Target: Cloudflare Pages static upload
- Build command: `npm run build`
- Output directory: `build`
- CLI: Wrangler (`npm run cf:*`)

This repo is intentionally configured for static output only. Do not assume Cloudflare Functions, Workers, or SSR are part of the deployment flow unless the repo instructions change.

### Cloudflare CLI Setup

Cloudflare's current Pages direct-upload flow uses `wrangler pages deploy` for prebuilt static assets and `wrangler pages dev` for local Pages-style serving. This repo now includes Wrangler as a dev dependency and exposes the common flows as npm scripts.

The Pages project is configured in [wrangler.toml](/home/mariogk/Projects/TerraScale/TerraScale.LandingPage/wrangler.toml:1). That file is the source of truth for the Pages project name, build output directory, compatibility date, and any shared or environment-specific bindings you add later.

1. Install dependencies:

```bash
npm install
```

2. Log in to Cloudflare in your local shell:

```bash
npm run cf:login
```

3. Create the Pages project once if it does not already exist:

```bash
npm run cf:project:create
```

That reads the Pages project name and compatibility date from `wrangler.toml`. The production branch defaults to `main`; override it with `CLOUDFLARE_PAGES_PRODUCTION_BRANCH` if needed when creating the project.

4. Deploy to production:

```bash
npm run cf:deploy
```

5. Deploy a branch preview:

```bash
npm run cf:deploy:preview
```

The preview deploy script uses the current git branch by default. Override it only when needed:

```bash
export CLOUDFLARE_PAGES_BRANCH=staging
npm run cf:deploy:preview
```

### Local Pages Preview

To verify the static build through the Cloudflare Pages CLI locally:

```bash
npm run cf:pages:dev
```

That builds the site and serves the generated `build/` output through `wrangler pages dev`.

### Non-Interactive CI Deploys

For CI or any non-interactive environment, provide:

- `CLOUDFLARE_API_TOKEN`: Cloudflare API token with `Account / Cloudflare Pages / Edit`.
- `CLOUDFLARE_ACCOUNT_ID`: Cloudflare account ID for the target Pages account.

Optional deploy metadata overrides:

- `CLOUDFLARE_PAGES_PRODUCTION_BRANCH`: override the production branch used only when creating the Pages project.
- `CLOUDFLARE_PAGES_BRANCH`: force the preview branch name.
- `CLOUDFLARE_PAGES_COMMIT_SHA`: override the commit SHA attached to the deployment.
- `CLOUDFLARE_PAGES_COMMIT_MESSAGE`: override the commit message attached to the deployment.
- `CLOUDFLARE_PAGES_COMMIT_DIRTY=1`: mark the deployment as dirty if you want Cloudflare to record that state explicitly.

The direct upload flow is documented by Cloudflare here:

- [Direct Upload](https://developers.cloudflare.com/pages/get-started/direct-upload/)
- [Use Direct Upload with continuous integration](https://developers.cloudflare.com/pages/how-to/use-direct-upload-with-continuous-integration/)
- [Local development with wrangler pages dev](https://developers.cloudflare.com/pages/functions/local-development/)

## Icons

- Add or update icon entries in `scripts/icons-manifest.mjs`
- Run `npm run icons:sync`
- Render icons through `src/components/Icon.svelte`
- Do not import runtime icon packages directly into components

## Assets

Static assets live in `static/` and are served from root-relative URLs such as `/logo-principal.svg` or `/icons/lucide/menu.svg`.
