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
- We are deploying to cloudflare pages

The entire app ships as static HTML/CSS/JS from `build/`. Do not introduce code paths that require a runtime server unless the repo deployment story is explicitly changed first.

## Git

- The user must send the ku link like `https://ku.mariogk.com/terrascale/issue/DSH-96` and then you need to extract the issue in this example DSH-96 and add to the prefix of the PR like DSH-96:{PR_TITLE}
- Always add `mariogk` as the reviewer and `terrascale-bot` as well
- Use 'fj' to interact with git and forgejo
- Always use the origin's main branch as the base of newer branches
- Always create a feature branch and a worktree when doing new features or fixes
  Example of branch names:
    - feat/{feature_name}
    - fix/{fix_name}
    - hotfix/{fix_name}
    - refactor/{refactor_desc}
- When the work is done create a PR into main when the work is ready to be merged.
- When pushing and creating the pull request make sure that the linter has no errors and that the `slow` tests passes,
- When creating PR send the link of that PR to the user.
- When creating a PR and the change has visual changes add multiple screenshots showing the changes that you made.