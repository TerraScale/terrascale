# TerraScale Landing Page

The official TerraScale marketing site, built as a fully prerendered SvelteKit app served as static assets by Cloudflare Workers.

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
npm run icons:sync
npm run cf:dev
```

## Deployment

The monorepo's `.github/workflows/landing-ci.yml` validates changes under
`landing-site/` and deploys green `main` to the existing Cloudflare Worker.
From the monorepo root, `make landing-test-fast` installs locked dependencies,
runs the tests, prerenders the site and validates the static output. Production
builds publish their monorepo commit in `/build-info.json`.

`wrangler.toml` serves `build/` as static assets only (no Worker script) on
`terrascale.tech` and `www.terrascale.tech`. Pricing and region catalogs remain
published at `/pricing.json` and `/regions.json`. The `/sdk` vanity route uses
Go 1.25+ subdirectory discovery for `sdk/go` in the monorepo; Go releases use
`sdk/go/v*` tags.

## Icons

- Add or update icon entries in `scripts/icons-manifest.mjs`
- Run `npm run icons:sync`
- Render icons through `src/components/Icon.svelte`
- Do not import runtime icon packages directly into components

## Assets

Static assets live in `static/` and are served from root-relative URLs such as `/logo-principal.svg` or `/icons/lucide/menu.svg`.

## Public regions and SDK samples

The [zero-configuration operating contract](AGENTS.md#zero-configuration-user-decision-2026-10-09-do-not-revert)
requires every serving region to be discovered automatically, with no regional
activation step. The checked-in `static/regions.json` is currently a marketing
catalog, not live node admission or evidence of which regions serve. An
upstream public discovery feed and its publication integration remain pending.
Do not describe manually editing the catalog as enabling a region or invent a
shipped automatic feed.

Displayed SDK samples currently accept a caller-supplied resolver and authorized
customer project/key. Automatic first-party endpoint and credential acquisition
is the operating contract; these samples do not implement that flow. Preserve
customer tenant scope, trusted HTTPS origins, redirect rejection and request
cancellation. No service secret belongs in a browser bundle, and sample keys
are not landing-site runtime configuration. Existing static-origin bypass and
launch-copy selectors remain inventoried for owning implementation changes;
this docs-only change does not remove them or prove a launch phase is deployed.
