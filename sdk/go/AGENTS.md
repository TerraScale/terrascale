AI IS NOT ALLOWED TO EDIT THIS FILE ONLY HUMANS CAN!

# Rules

- Never add AI attribution to commits, PR titles, or PR descriptions: no `Co-Authored-By:` trailers, no `🤖 Generated with [Claude Code]` or similar notices.
- This project must use Go and the go-modern-guidelines plugin.

## Agent model routing

- Short, bounded delegated agent work: Claude Haiku 5.5 (`claude-haiku-5-5`).
- If `claude-haiku-5-5` is unavailable: Codex GPT-6.1-Sol (`gpt-6.1-sol`), after confirming the ID in the live model catalog.
- Advisor work and tasks expected to take more than 10 minutes: Claude Opus 5.5 (`claude-opus-5-5`). The Haiku rule covers only short, bounded work.
- Never use Haiku 4.5 (`claude-haiku-4-5`, including dated IDs such as `claude-haiku-4-5-20251001`), and never fall back silently to an older Haiku. If no allowed model is available, report it instead of substituting one.
- Run every T3 Code thread in full-access mode, within the existing tool safety rules.

## Local CPU Priority

- Prefix every local build, test, lint, benchmark, dev server, and watcher with `nice -n 19`, for example `nice -n 19 make test-fast`.
- In CI (`CI=true`), use full CPU priority. Never add `nice` or `renice` to CI workflows, Makefiles, scripts, or Containerfiles.

## Zero configuration (user decision 2026-10-09, do not revert)

- Every feature is always on. No feature flags, on/off variables, opt-in settings, boolean enabled configuration keys, deploy toggles or kill switches in code, config or CI. Delete switches and their guarded paths.
- Every region always serves. ts-orch discovers regional nodes through NetBird and manages workloads on OCI.
- One fixed global API and auth origin, with no regional discovery or failover.
- Production service credentials arrive from OCI Vault in gru via instance principal. Nobody copies secrets by hand. Customer credentials authorize access.
- Values have one correct setting in code. Expose no tuning knobs. Fakes, demo stores and skip-auth belong only in test code and test binaries.
- CI rejects new switch-style configuration; ts-reviewer requests changes. Only unavoidable third-party names may have a narrow allowance with a one-line reason.

This section is the operating contract, not evidence that every runtime flow has shipped. See [README.md](README.md) for current SDK and qualification gaps. Existing production switches are inventoried for their owning implementation threads. Documentation changes must not invent APIs or bypass authorization, tenant scope, registered callbacks, payment consent, or safety guards.
