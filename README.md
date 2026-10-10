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

This repository's Forgejo Actions workflow (`.forgejo/workflows/ci.yml`)
verifies every push to `main` and every pull request targeting `main`
(checkout, `npm ci`, `npm run check`, `npm run lint`). A merged push to
`main` then publishes the verified `build/` output to the Cloudflare Pages
project `ts-landing` (production domains `terrascale.tech` and
`www.terrascale.tech`) with the Wrangler pinned in `package-lock.json`, and
smoke-checks the production URL after the upload. A missing credential fails
the deploy step closed: a main push can never report green without
publishing.

The current deploy step reads two repository Actions secrets:

- `CLOUDFLARE_API_TOKEN` — Pages edit access on the account
- `CLOUDFLARE_ACCOUNT_ID` — the account that owns the Pages project

Under the TS-795 operating contract, the vendor-issued token is stored once
in OCI Vault and publication credentials are delivered to the authorized job
automatically. First-party secrets are created and delivered automatically;
no manual Actions-secret copying belongs in setup. This documentation change
does not implement or verify that delivery integration. The workflow must
continue to fail closed when publication credentials are missing. Use the
`cf` CLI for authorized Cloudflare account operations; see
[AGENTS.md](AGENTS.md#cloudflare-use-the-cf-cli). Never put publication credentials
in the static site output.

The `wrangler.toml` in this working tree pins the Pages project name for
the local preview and plays no part in the pipeline.

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

## Public regions and SDK samples

The [zero-configuration operating contract](AGENTS.md#zero-configuration-user-decision-2026-10-09-do-not-revert)
requires every serving region to be discovered automatically, with no regional
activation step. The checked-in `static/regions.json` is currently a marketing
catalog, not live node admission or evidence of which regions serve. An
upstream public discovery feed and its publication integration remain pending;
until admitted, catalog drift checks validate the existing static artifact.
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

## Real sample contract tests

`npm run test:slow` runs the actual TypeScript and compiled C# samples against a local TerraBase
release over HTTPS. It covers successful create/query, failed create stopping the
query, failed query cancellation, and both requests' Bearer/redirect options.
It creates temporary local credentials and databases; it never calls production
or substitutes API responses.

Download the pinned Linux amd64 release using your existing Forgejo login:

```bash
fj release asset download -r TerraScale/terrabase \
  ci-ff256067015433c8c40b59879f1282d2a40480ce-run-13258 \
  terrabase-linux-amd64 -o /tmp/ts502-terrabase
chmod +x /tmp/ts502-terrabase
TERRABASE_TEST_BINARY=/tmp/ts502-terrabase nice -n 19 npm run test:slow
```

The runner verifies SHA-256
`e07d173fc2bbab19dcb1fde8b121b0207b07d55452a5b19a406871d220e05e13`
before execution. Node.js, OpenSSL, a .NET SDK targeting net10.0, and the pinned binary are required; missing
or mismatched inputs fail the suite. The runner chooses available ports and
stops its own server afterward. This release's HTTPS listener binds all
interfaces, so use an isolated test environment when network isolation is needed.
