# Static site dependency compatibility

The landing site uses stable SvelteKit 3.1, adapter-static 4, Svelte 5.57,
Vite 8.3 and vite-plugin-svelte 7.3. Node 24 is pinned in `.node-version`;
SvelteKit requires Node >=22.17, Svelte >=5.57.1, TypeScript 6 and Vite >=8.0.12.
The committed npm lockfile records the complete install graph.

Following the [SvelteKit 3 migration guide](https://svelte.dev/docs/kit/migrating-to-sveltekit-3):

- Framework/preprocessor/adapter configuration moves from `svelte.config.js`
  into the Vite plugin. The existing `@` source alias lives in Vite and TypeScript.
- TypeScript extends `$app/tsconfig` with explicit project includes/excludes.
- Layout metadata and active navigation read `$app/state` with Svelte 5 runes,
  so client navigation updates canonical URLs, page titles and active links.
  The header closes its mobile menu after navigation through `afterNavigate`.
- Adapter-static 4 targets SvelteKit 3. Root prerendering and build-time rendering
  remain enabled, with strict adapter defaults and no SPA fallback. All five
  pages and the robots/sitemap resources are emitted to `build/`.

SvelteKit 3 also changes enhanced forms and restricts external redirects. This
site has no form actions or SvelteKit runtime redirects. The existing Cloudflare
`static/_redirects` file is copied unchanged. Deployment still serves only static
assets; this upgrade adds no Worker script, server or request-time application.

SvelteKit 2.70.2 fixes the Accept-header ReDoS but does **not** satisfy the two
low-severity advisories whose patched floor is 3.0.0. The stable 3.x upgrade covers
all three findings. Remove the previous cookie/devalue overrides so SvelteKit
can resolve its supported cookie 2 and current devalue versions.

Wrangler 4.152 remains the deployment tool. Its Miniflare dependency still pins
undici 7.29.1, so the existing override advances to compatible 7.30.1, fixing the
reported content-encoding DoS, EventSource memory and integrity-token advisories.
Do not downgrade Wrangler to npm audit's suggested old parent or force undici 8.
Wrangler's upstream Miniflare prerelease is transitive to stable Wrangler.

The `Test` workflow caches npm downloads, audits every tracked package-lock
(including future example directories) and fails on high/critical advisories
before a clean install and production build. There was no existing test job;
CodeQL and Cloudflare Workers Builds are preserved. The build runs the existing
pricing/CSP tests and checks the emitted static pages, metadata, active navigation,
inline script CSP hashes and unchanged public resources.

A clean advisory scan concerns the resolved build/deployment graph. It is not
proof of production exploitability or deployment. Live publication validation
belongs to the normal approved postmerge workflow.
