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
npm run cf:pages:verify
npm run cf:pages:dev
npm run cf:deploy
npm run cf:deploy:preview
```

## Deployment

- Target: future Cloudflare Pages Git-integrated delivery (Direct Upload retired)
- Build command: `npm run build`
- Output directory: `build`
- CLI: Wrangler (`npm run cf:*`)

This repo is intentionally configured for static output only. Do not assume Cloudflare Functions, Workers, or SSR are part of the deployment flow unless the repo instructions change.

### Cloudflare Pages delivery

`cf:deploy`, `cf:deploy:preview`, and `cf:project:create` are intentionally retired and always fail before any network or Wrangler invocation. Cloudflare documents Direct Upload with a Cloudflare Pages **Edit** token, while its Pages API identifies deployment creation as requiring **Pages Write**; that permission cannot be safely restricted to deployment-only and can affect project state. [Direct Upload with CI](https://developers.cloudflare.com/pages/how-to/use-direct-upload-with-continuous-integration/) and [Pages API permissions](https://developers.cloudflare.com/pages/configuration/api/) describe these capabilities.

`npm run cf:pages:verify` is an optional, non-authorizing diagnostic only. It reads Pages metadata from the fixed Cloudflare API origin using `CLOUDFLARE_ACCOUNT_ID` and a separate `CLOUDFLARE_PAGES_READ_TOKEN`; it never accepts or sends a Pages Write token.

The safe future design is a **new** Git-integrated Pages project connected to a supported hosted GitHub or GitLab mirror, with its source production branch protected. Cloudflare supports GitHub and GitLab integrations, but not self-hosted GitHub/GitLab instances, so Forgejo cannot connect directly. See [Git integration](https://developers.cloudflare.com/pages/configuration/git-integration/). Creating, migrating, or retargeting a Pages project is not authorized by this repository workflow.

Release candidates remain verified artifacts: they package the static site and test evidence, but they are not a deployment. Activation requires explicit authorization to provision a new Git-integrated Pages project, connect a supported hosted mirror, protect its production branch, and record the resulting deployment endpoint before replacing the blocked promotion workflow.

### Local Pages Preview

To verify the static build through the Cloudflare Pages CLI locally:

```bash
npm run cf:pages:dev
```

That builds the site and serves the generated `build/` output through `wrangler pages dev`.

### CI credentials

Normal CI must not receive a Cloudflare Pages Write token. If read-only metadata diagnostics are needed, provide only `CLOUDFLARE_ACCOUNT_ID` and `CLOUDFLARE_PAGES_READ_TOKEN` to the explicit diagnostic command; the normal test/build pipeline needs neither.

## Icons

- Add or update icon entries in `scripts/icons-manifest.mjs`
- Run `npm run icons:sync`
- Render icons through `src/components/Icon.svelte`
- Do not import runtime icon packages directly into components

## Assets

Static assets live in `static/` and are served from root-relative URLs such as `/logo-principal.svg` or `/icons/lucide/menu.svg`.
