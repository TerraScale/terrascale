# TerraScale Landing Page Agent Notes

## Stack

- Use SvelteKit with Svelte 5 and TypeScript for UI work in this repository.

## Deployment

- Deployments use the simpler Cloudflare Pages static upload flow.
- Prefer solutions that work cleanly as a static site build output.
- Do not assume SSR, server adapters, or Cloudflare Workers are part of the deployment setup unless the repo instructions are changed.

## Icons

- Source icons from Iconify, but do not render them through an npm runtime or build plugin in the app.
- Host checked-in individual SVG files in `public/icons/`; do not use sprite sheets for this repo.
- Render icons through the local Svelte wrapper and registry, not by importing icon packages directly in components.
- When adding or changing icons, update `scripts/icons-manifest.mjs` and run `npm run icons:sync`.
- Prefer `lucide` icons from Iconify for generic UI glyphs and `simple-icons` from Iconify for brand/social logos.
- Keep icon SVGs monochrome and `currentColor`-driven so Tailwind/text color classes control their appearance.
- Do not add or prefer React, Lucide, or runtime/build icon packages for new work here unless these repo rules are changed first.
