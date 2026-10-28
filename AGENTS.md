AI IS NOT ALLOWED TO EDIT THIS FILE ONLY HUMANS CAN!

# Rules
- No AI attribution in commits, PR titles or PR descriptions: no `Co-Authored-By:` trailers, no `🤖 Generated with [Claude Code]` notices.
- Use VitePress.
- This is a public docs repo. Keep internal information out: no internal database names, hosts, accounts, topology or similar.

## Local CPU priority

- Prefix every local build, test, lint, benchmark, dev server and watcher with `nice -n 19`, e.g. `nice -n 19 npm run build`.
- In CI (`CI=true`), use full priority. Never add `nice` or `renice` to CI workflows, Makefiles, scripts or Containerfiles.

## Pull requests

- Base all changes on the most recent `main`. Fetch `origin/main` before starting and rebase onto the latest `origin/main` before merging; CI must pass on that exact latest head.
- Upload PNGs with `node scripts/upload-pr-images.mjs <PR-number> <file.png> [more.png ...]`; paste its Markdown into the PR body. The helper creates the orphan `pr-assets` branch on first use and preserves existing images. Use descriptive alt text and distinct filenames for each page/revision/viewport/theme.

### PR previews (required for every visible change)

- Every PR that changes anything a user sees (pages, components, styles, layout, copy, emails, generated pages, maps, charts) MUST embed previews in the PR body. A visible change without previews is not ready to merge.
- Before and after screenshots of each changed page and state, desktop (1440 px wide) and mobile (390 px wide); light and dark too where the app has both. Add a short video or GIF for interactions (toggles, menus, flows, animations).
- Capture from the real running build of the exact PR head (dev server, preview deploy or live preview), never mockups. Use synthetic or demo accounts; no secrets, tokens or real customer data on screen.
- Upload: commit the images to this repo's orphan branch `pr-assets` under `<pr-number>/` (first time: `git switch --orphan pr-assets`); never put them on the PR branch or `main`, never merge `pr-assets`. Embed each as `![<page> <state> after, desktop](https://raw.githubusercontent.com/TerraScale/<repo>/pr-assets/<pr-number>/<file>.png)` and check that every URL returns 200 before posting.
- When a later push changes the visuals, replace the previews. PRs with no visible change say "No visual change" in the body.

## Cloudflare: `cf` CLI

- Use the `cf` CLI (npm package `cf`, v1.0.0-beta.10) for DNS, zones, origin CA and client certificates, SSL/TLS, cache, rules, analytics and API tokens. Workers deploys keep using `wrangler`.
- Run `cf auth whoami` first. It must show `"authenticated": true`.
- Never print tokens or the config file. Show the plan before any change. Deleting zones, production DNS records or Workers needs the user's confirmation. Record changed Cloudflare object IDs (never secrets) in the PR or runbook.

## Zero configuration (user decision 2026-10-09, do not revert)

- Every feature is always on. No feature flags, on/off variables, opt-in settings, boolean enable keys, deploy toggles or kill switches in code, config or CI. Delete existing switches and their guarded paths.
- Every region always serves. It starts serving as soon as its machines boot, with no per-region enable step.
- Production secrets live in the platform secret store and are never copied by hand. Vendor-issued keys (such as Stripe and Cloudflare) are set the same way.
- Each value has one correct setting in code. Expose no tuning knobs. Fakes, demo stores and skip-auth belong only in test code and test binaries.
- CI rejects new switch-style configuration. Only unavoidable third-party names get a narrow allowance, with a one-line reason.

Publish this contract without private topology, credentials or internal implementation details. Production publication credentials come from the platform secret store, never this repo.
