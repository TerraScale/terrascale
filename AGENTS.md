<!-- shared-rules:start -->

## Shared TerraScale rules

### Shipping code

- No PRs, reviews or merge queue. Push to `main`. Ignore outside contributors' PRs unless asked.
- Fetch `origin` first. Branch from fresh `origin/main` in your own worktree or clone, outside shared checkouts. Commit small changes; no AI attribution.
- Before every push: `git fetch origin && git rebase origin/main`, then pass the changed area's Fast tests on a dev host with `ts-heavy-any <cmd> --repo <repo> --branch <ref>`. Push `git push origin HEAD:main`; rebase and retry a non-fast-forward rejection. Never force-push or delete `main`.
- CI runs on every main push; deploy only green main. If your push makes main red, fix or revert within about 30 minutes. For a red main you did not cause, wait for its owner's fix and rebase.
- Heavy builds, tests, benchmarks and stress repeats use `ts-heavy-any`, never GitHub Actions. Use mariogk-dev or beta; never run anything on or SSH to mariogk-desktop.
- Prove flaky fixes with 5 repeats; benchmarks with 3 measured repeats in `benchmark/bench/`. For page changes, report before/after screenshots.
- StorageFormat.cs bumps and production resets belong to the lane in `.cos/lanes.md`; other lanes put `Needs format bump` in the commit message.

### Agents and decisions

- Use gpt-6.1-sol (Codex, high effort; low for work under 10 minutes) for most work. Use claude-haiku-5-5 for bounded work of at most 20 minutes; never Haiku 4.5 or the `haiku` alias. Reserve claude-opus-5-5 for engine/replication, auth/security design, billing correctness, production infrastructure or work Sol already failed.
- Decide and continue. Record `DECISION: <choice>. Reason: <why>` in commits.
- Workers never ask the user. Send the parent `NEED Q-<ID>: <question> | options: 1) <recommended> 2) ... | DECIDED: 1, continuing`. Only the COS asks the user.
- Block only for a user-only secret/login, new paid capacity, or deleting serving infrastructure or unrecoverable data. Start login device flows yourself, show URL/code, then finish after the user enters them.

### Secrets and hard gates

- Runtime secrets: OCI Vault in gru via instance principal. CI secrets: GitHub Actions. Dev secrets: `ts-dev-secret get <name> | <consumer>` or `ts-dev-secret env <name> -- <cmd>`.
- Never expose secret values in chat, arguments, history, logs, git or commits. Use stdin or a 0600 file. Credential installs are one-command handoffs the user runs.
- Ask first before new paid capacity, security permissions or credentials.
- ts-auth and ts-dashboard store data in TerraBase through ts-dotnet; keep the SDK current with TerraBase's API.
- Outside the monorepo, never download or run TerraBase binaries in tests, CI or helpers; use HTTP fakes or contract fixtures. Live ts-status API monitoring is allowed. Never publish TerraBase binaries.
- Use GitHub and `gh`; no polling loops. Never revive ts-reviewer or ts-cos.

<!-- shared-rules:end -->

# ts-dotnet

## Layout and checks

- SDK: `src/TerraScale.DotNet`; TUnit tests: `tests/TerraScale.DotNet.Tests`; contract fixtures: `contracts/`.
- Keep related files and services together using feature-sliced design.
- Use the SDK pinned in `global.json`; nullable types, analyzers and warnings as errors stay enabled.
- Build: `dotnet build TerraScale.DotNet.slnx --configuration Release`.
- Fast: `dotnet run --project tests/TerraScale.DotNet.Tests --configuration Release --framework net10.0`.
- Release checks: `bash scripts/check-release.sh`.

## API rules

- Land the owning public REST, WebSocket or SSE contract before its SDK operation. Verify method/message, versioned endpoint, operation identity, parameters, media types, field types/nullability, security/scope, success/error/partial outcomes, retry/idempotency, pagination/checkpoints and generated contract/route parity. Never invent server behavior.
- APIs are async and cancellation-aware. Never use `.Result`, `.Wait()` or sync-over-async. Clients are thread-safe after construction; never dispose caller-owned `HttpClient`.
- Do not serialize independent caller work without a correctness reason. Bound batch/pagination concurrency and backpressure; stream with `IAsyncEnumerable<T>`.
- Retry only documented transient failures safe to repeat. Mutations need stable idempotency. Respect deadlines, cancellation, `Retry-After` and overload; never auto-retry validation, authentication, authorization or deterministic conflicts.
- Use discoverable TerraScale names and immutable records. Validate options at construction with actionable errors; document public APIs with XML docs.
- Fix first-party timeout, retry, concurrency and buffer limits in code. Preserve caller cancellation and security validation.
- No console output or process-wide mutable state. Only an explicitly owned reactive object may start and dispose hidden background work.

## Configuration

- Keep features always on; no feature flags, enable switches, opt-ins or kill switches. Keep one correct value in code, with no new tuning knobs.
- Fakes, demo stores and skip-auth belong only in test code and test binaries.
- Keep the switch-style configuration check; unavoidable third-party names need a narrow allowance and a one-line reason.
- README.md records implementation and qualification gaps. Do not claim unshipped behavior or bypass authorization, tenant scope, registered callbacks, payment consent or safety guards.
