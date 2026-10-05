AI IS NOT ALLOWED TO EDIT THIS FILE ONLY HUMANS CAN!

# Rules
- Never add AI attribution to commits, PR titles, or PR descriptions: no `Co-Authored-By:` trailers, no `🤖 Generated with [Claude Code]` or similar generated-by notices.
- We need to use vitepress
- This is a public docs repo, we should not have internal information here like which database we are using interally and similar information
- This repo must not have internal information

## Local CPU Priority

- When building or testing locally, run every build, test, lint, and benchmark
  command at low CPU priority by prefixing it with `nice -n 19` (for example
  `nice -n 19 make test-fast`). Local dev servers and watchers count too.
- In CI (`CI=true`) use full CPU priority: do nothing. Never add `nice` or
  `renice` to CI workflows, Makefiles, scripts, or Containerfiles; the low
  priority is applied by whoever runs the command locally.

## Cloudflare: use the `cf` CLI

The Cloudflare CLI `cf` (npm package `cf`, v1.0.0-beta.10, installed through the mise Node toolchain) is installed and logged in on all 3 T3 hosts (delta, beta, mariogk-dev) as `mariogk01@gmail.com`, whose account owns the `terrascale.tech` zone.
- Use `cf` for Cloudflare work: DNS records, zones, origin CA / client certificates, SSL/TLS and cache settings, rules, analytics, API tokens. Workers deploys keep using `wrangler`.
- Before you start, run `cf auth whoami`. It must show `"authenticated": true`.
- Never print tokens or the config file. Print the plan before you change anything. Deleting zones, production DNS records or Workers needs the user's confirmation. Record changed Cloudflare object IDs (not secrets) in the PR or runbook.
