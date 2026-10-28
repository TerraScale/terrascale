AI IS NOT ALLOWED TO EDIT THIS FILE ONLY HUMANS CAN!

# Rules
- No AI attribution in commits, PR titles or PR descriptions: no `Co-Authored-By:` trailers, no `🤖 Generated with [Claude Code]` notices.
- Use VitePress.
- This is a public docs repo. Keep internal information out: no internal database names, hosts, accounts, topology or similar.

## Local CPU priority

- Prefix every local build, test, lint, benchmark, dev server and watcher with `nice -n 19`, e.g. `nice -n 19 npm run build`.
- In CI (`CI=true`), use full priority. Never add `nice` or `renice` to CI workflows, Makefiles, scripts or Containerfiles.

## Pull requests

- Every PR that changes what a reader sees (pages, navigation, styles, components, generated pricing/limits pages) MUST embed before/after screenshots of each changed page in the PR body: desktop and mobile, each in light and dark mode, with descriptive alt text. For shared UI changes, capture every affected page.
- Capture the real built site, using `nice -n 19 npm run build` and `nice -n 19 npm run preview`, or the deployed Workers preview. Capture the base revision before editing and the PR revision after; label the page, revision, viewport and theme.
- Upload PNGs with `node scripts/upload-pr-images.mjs <PR-number> <file.png> [more.png ...]`; paste its Markdown into the PR body. It commits only images to the dedicated orphan `pr-assets` branch and prints public raw URLs. Never commit screenshots to main or the PR branch. Use distinct filenames for each page/revision/viewport/theme.
- Text-only, CI and repository-file PRs without a rendered change say `No visual change` in the PR body.

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
