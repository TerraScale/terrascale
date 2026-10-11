AI MAY EDIT THIS FILE ONLY WITH HUMAN PERMISSION! If a human or the user requests a change, the AI may edit this file. If the AI needs to change it, it MUST ask the user first.

# Rules
- Never add AI attribution to commits, PR titles, or PR descriptions: no `Co-Authored-By:` trailers, no `🤖 Generated with [Claude Code]` or similar generated-by notices.

## Local CPU Priority

- When building or testing locally, run every build, test, lint, and benchmark
  command at low CPU priority by prefixing it with `nice -n 19` (for example
  `nice -n 19 make test-fast`). Local dev servers and watchers count too.
- In CI (`CI=true`) use full CPU priority: do nothing. Never add `nice` or
  `renice` to CI workflows, Makefiles, scripts, or Containerfiles; the low
  priority is applied by whoever runs the command locally.

## Zero configuration (user decision 2026-10-09, do not revert)

- Every feature is always on. No feature flags, on/off variables, opt-in settings, boolean enabled configuration keys, deploy toggles or kill switches in code, config or CI. Delete switches and their guarded paths.
- Every region always serves. NetBird network membership admits a node automatically; it discovers every region, infers its own region from where it runs and starts serving without a per-region enable step.
- OCI Vault automatically creates, delivers and configures every first-party production secret. Vendor-issued keys (such as Stripe and Cloudflare) are stored once in Vault and picked up automatically; nobody copies secrets by hand.
- Values have one correct setting in code. Expose no tuning knobs. Fakes, demo stores and skip-auth belong only in test code and test binaries.
- CI rejects new switch-style configuration; ts-reviewer requests changes. Only unavoidable third-party names may have a narrow allowance with a one-line reason.

Shared libraries must preserve automatic regional discovery and OCI Vault secret delivery for consuming production services.

### Migration scope and safety

This is the required operating contract, not a claim that every current implementation already meets it. TS-795 changes documentation and policy; existing production switches, tuning inputs and secret-delivery gaps remain inventory for later COS removal work. TS-796 removes the orchestrator; TS-773 handles TerraBase. Do not remove product code or configuration keys as part of TS-795.

Always-on capability does not bypass authentication, customer consent, repository permissions, credential revocation, validation, resource isolation, backups or reviewed recovery. Preserve branch/event identity, quality gates and failure/cleanup conditions in CI.
