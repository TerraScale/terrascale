AI IS NOT ALLOWED TO EDIT THIS FILE ONLY HUMANS CAN!

# Rules

## Develop
- Always create a feature called feat/{feature_name} fix/{fix_name} and then we create a PR into main when the work is ready to be merged when pushing and creating the pull request make sure that the linter has no errors and that all tests passes, when creating pull request send the link of that PR to the user.
- If the user requests a preview url to check the work that is current made you need to start the dev env and send the user a tailscale dns to access it and test if it works before sending to the user if it doesnt work fix it
- Do not use worktrees
- When running a dev server it must always be accesible via the tailscale net and it must have hmr turned for really fast development

# Public URL
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
