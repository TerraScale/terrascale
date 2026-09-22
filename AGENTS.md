AI IS NOT ALLOWED TO EDIT THIS FILE ONLY HUMANS CAN!

# Rules

FOR NOW WE MUST USE: 
TS_DASHBOARD_PRODUCTION_DEMO=1
Everything must work in memory account creation and etc

## Stack
- We need to use C#, RazorSlice and datastar
- The Dashboard must not depend on terrabase(project) it should depend on ts-dotnet instead


## Preview

- If the user requests a preview url to check the work that is current made you need to start the dev env and send the user a tailscale dns to access it and test if it works before sending to the user if it doesnt work fix it
- When running a dev server it must always be accesible via the tailscale net and it must have hmr turned for really fast development
- Make sure that the urls are different as we are using worktrees so the ports needs to be diff

# Deploy

- Fly.io

# Ownership

## Owner of
- Authentication via ts-auth
- MCP endpoints and MCP logic so the user can interact with the dashboard with an AI
- Organizations, Projects, Users, Payments, Plans
- Stripe management

## NOT AN OWNER OF
- Usage metrics
- Regions like available regions their latency and etc (This comes from terrabase)

# State 
- Stateless all state must live in the database via ts-dotnet

# Test

- We need to have a test that tests the whole flow from creating the account and then using the ts-typescript sdk to interact with the database and make sure it is working correctly


## Feature Slices

- Each product area owns its components and abstraction on different folders

## Design System

- We must use a Single CSS file

## Brand Assets

- TerraScale product logos, brand marks, wordmarks, and favicons must be served
  from SVG files under `public/brand` or another committed asset file.
- Do not inline TerraScale logo or wordmark SVG markup in React components,
  route files, CSS, or generated templates.
- Shared brand components may wrap those files with `<img>` for sizing,
  accessibility, and reuse, but the SVG path/polygon data must stay in the asset
  files.
- Use the same file-backed brand assets consistently on auth pages, the app
  shell, dashboard surfaces, and metadata/icon declarations.

## Dotted Map

- Use `dotted-map` only for map visualization
- Lazy-load map code and generated map data on routes that need it.
- Provide accessible non-map controls as the canonical input.
- Add to SVG the information needed so to make it easier to use the map on the code

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
- When pushing and creating the pull request make sure that the linter has no errors and that the `slow` and `fast` tests passes and run green.
- When creating PR send the link of that PR to the user.
- When creating a PR and the change has visual changes add multiple screenshots showing the changes that you made.