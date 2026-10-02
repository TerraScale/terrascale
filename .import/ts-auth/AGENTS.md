# ts-auth Agent Rules

> [!IMPORTANT]
> **AI agents MUST NOT edit this file. Only humans may modify it.**

## Project

`ts-auth` is the hosted identity provider for the TerraScale platform, in the same product category as Clerk, Auth0, and WorkOS.

It is built on **TerraBase through the `ts-rust` SDK**.

Prioritize security, tenant isolation, correctness, and interoperability over convenience or performance shortcuts.

## Local CPU Priority

- When building or testing locally, run every build, test, lint, and benchmark
  command at low CPU priority by prefixing it with `nice -n 19` (for example
  `nice -n 19 make test-fast`). Local dev servers and watchers count too.
- In CI (`CI=true`) use full CPU priority: do nothing. Never add `nice` or
  `renice` to CI workflows, Makefiles, scripts, or Containerfiles; the low
  priority is applied by whoever runs the command locally.

## Stack and Dependencies

- **Rust** is required for application and backend code.
- Use **Datastar** for server-driven UI and its **Rust SDK** for backend integration.
- Use the repository’s Rust templating approach for HTML; keep business logic out of templates.
- HTML, CSS, and minimal JavaScript are allowed where needed for browser behavior and tooling. Do not introduce another backend runtime or competing frontend framework.
- All APIs MUST have **OpenAPI** documentation rendered through **Scalar**.
- Use the latest stable, mutually compatible dependencies. Verify compatibility before upgrading.
- Prefer mature, maintained, well-tested crates over custom implementations.
- Keep dependency manifests and `Cargo.lock` synchronized; builds MUST remain reproducible.

## General

- Keep changes focused on the assigned issue and follow repository conventions.
- Never add AI attribution, generated-by notices, or co-author trailers to commits, PR titles, or PR descriptions.
- Never invent APIs, SDK capabilities, issue IDs, commands, or successful test results.
- Preserve unrelated changes; never discard, overwrite, or commit another contributor’s work.
- Do not claim feature completeness, protocol compliance, or security certification without supporting implementation and verification.
- Do not introduce production authentication bypasses, hardcoded credentials, or silently enabled demo behavior.

## Architecture

### Storage and State

- All TerraBase access MUST go through `ts-rust`.
- Do not import or embed the TerraBase server project or bypass the SDK with direct storage access.
- Persistent identity and authentication state MUST live in TerraBase.
- Keep service instances stateless; do not require server affinity or process-local state for correctness.
- Caches MAY improve performance but MUST NOT override authorization, expiry, or revocation requirements.
- Keep signing keys and encryption secrets in the approved secret-management system, separate from source code and public configuration.
- Never silently substitute in-memory persistence when the database is unavailable.

### Consistency

- Verify the actual consistency, uniqueness, and atomic-operation guarantees available through `ts-rust`.
- Single-use challenges, credential updates, refresh-token rotation, and identity uniqueness MUST remain correct under concurrent requests.
- Do not implement security-critical operations as an unprotected read followed by a write.
- Process-local locks MUST NOT be treated as coordination between service instances.
- If required storage guarantees are unavailable, report the limitation and require an approved coordination design. Never weaken the security requirement silently.
- Document and test revocation behavior, cache staleness limits, and failure handling.

### Organization

- Organize code by feature, with clear boundaries between HTTP handlers, application logic, persistence, and presentation.
- Share authentication and authorization logic between APIs and UI flows.
- Keep billing, product-specific business logic, and TerraBase server internals outside this service.
- Add abstractions only when they express a real boundary or eliminate meaningful duplication.

## Identity and Security

### Credentials

- Use established cryptographic libraries. Do not implement custom cryptography or token-signing algorithms.
- Hash passwords with **Argon2id**, unique salts, and explicitly configured parameters appropriate for production resources.
- Never store plaintext or reversibly encrypted passwords.
- Generate tokens and challenges with a cryptographically secure random source.
- Store hashes of opaque bearer secrets when only verification is required; encrypt secrets that must be recoverable.
- Verification, recovery, and authorization codes MUST expire and be single-use.
- Require appropriate reauthentication before sensitive credential or account changes.

### Access Control

- Authenticate and authorize every protected operation on the server.
- Deny access by default and grant only the permissions required.
- Validate tenant, client, user, and resource relationships; never trust a submitted identifier as proof of access.
- Keep tenants isolated across database queries, caches, tokens, UI responses, and background operations.
- Rate-limit authentication, recovery, verification, and token endpoints.
- Avoid account-enumeration leaks through response bodies, status codes, or obvious timing differences.
- Fail closed when required authentication or authorization checks cannot be completed.

### Sessions and Browser Security

- Require HTTPS in production.
- Use `Secure`, `HttpOnly` session cookies with an explicit `SameSite` policy appropriate for supported authentication flows.
- Protect cookie-authenticated mutations against CSRF.
- Use explicit origin and redirect allowlists; never allow arbitrary redirects.
- Rotate session identifiers after authentication and privilege changes.
- Enforce expiry and documented logout/revocation behavior server-side.
- Do not persist bearer credentials in browser storage or expose server secrets in HTML, Datastar signals, or client bundles.
- Never log passwords, tokens, authorization headers, session cookies, or recovery secrets.

### Authentication Protocols

When implementing OAuth or OpenID Connect:

- Follow the applicable specifications and current security guidance rather than inventing equivalent-looking protocols.
- Use Authorization Code with PKCE for interactive client authorization; do not implement implicit or resource-owner-password grants.
- Validate registered redirect URIs and enforce the required client, PKCE, state, and nonce bindings.
- Explicitly configure trusted issuers; do not derive them from untrusted request headers.
- Validate token signatures, allowed algorithms, issuer, audience, expiry, and intended token use.
- Do not accept an ID token as an API access token.
- Protect refresh tokens against replay and document rotation, expiry, and revocation behavior.
- Support safe signing-key rotation without exposing private key material.
- Test supported flows with an independent client before claiming interoperability.

## APIs: OpenAPI and Scalar

### API Contract

- Maintain a canonical OpenAPI document covering every implemented API operation.
- Document methods, paths, parameters, request bodies, responses, errors, authentication requirements, and permissions.
- Use stable, unique operation IDs.
- Prefer shared Rust types and generated schemas over separately maintained definitions.
- Update implementation, OpenAPI, examples, and integration tests in the same change.
- Preserve standards-required content types and error formats; do not force every endpoint into a custom JSON convention.
- OpenAPI security declarations MUST correspond to actual server-side enforcement.
- Treat breaking API changes as explicit compatibility decisions, not incidental refactors.

### Documentation

- Serve the OpenAPI document at `/openapi.json` and Scalar at `/docs`, unless the repository already establishes different paths.
- Scalar MUST load the same contract validated by CI.
- Keep examples realistic but synthetic; never include real credentials or customer data.
- Document internal and administrative APIs without exposing restricted documentation publicly.
- Do not preload production credentials, persist authorization secrets, or route authenticated documentation requests through third-party proxies.
- Self-host documentation assets where practical and resolve their versions reproducibly.

### Validation

- Validate OpenAPI structure and references in CI.
- Check implemented routes and representative responses against the contract.
- Test that Scalar loads the schema without browser errors.
- Include API contract and documentation smoke checks in `fast`.

## Datastar UI

- Render pages and HTML fragments on the Rust backend.
- Use Datastar for interactions and incremental updates; use SSE only where appropriate.
- Keep authoritative identity state and permission decisions on the server.
- Treat browser signals as untrusted input. Send only the fields required for each action.
- Escape rendered values and keep untrusted data out of executable Datastar expressions.
- Never echo passwords or authentication secrets into rendered HTML or update streams.
- Use the documented CSP-compatible configuration; do not weaken security headers merely to make an interaction work.
- Provide accessible forms, keyboard navigation, visible focus states, and usable validation messages.
- Preserve behavior after repeated updates, reconnects, and navigation.
- Reuse committed brand assets rather than duplicating logo markup.

## Development and Previews

### Builds

- Use existing repository scripts and Makefile targets when available.
- Keep formatting, build, Clippy, and dependency-security checks passing.
- Do not suppress warnings or security findings without a documented justification.
- Keep development credentials, signing keys, and data separate from production.

### Previews

- Development servers MUST be accessible through the intended Tailscale network.
- Enable automatic rebuilds and browser reload; use HMR where supported.
- Assign each active worktree its own available application and reload ports.
- Never stop another worktree’s processes or reuse occupied ports.
- Use HTTPS for previews that exercise secure cookies or authentication redirects.
- Register exact development callback URLs; do not weaken production redirect validation for previews.

When a preview is requested:

1. Start the environment for the correct branch and worktree.
2. Verify the actual Tailscale DNS URL, including HTTPS and the required port.
3. Test the changed functionality, authentication redirects, cookies, and reload behavior.
4. Fix failures before sharing the URL.
5. Send the verified preview URL to the user.

Never invent a hostname, send localhost as a remote preview, or present an unverified environment as working.

## Testing

### Structure

- Maintain exactly two integration-test projects: **`fast`** and **`slow`**.
- Use integration and end-to-end tests only. **Do NOT create unit tests.**
- All feature tests MUST belong to one of these projects; shared utilities MUST NOT become a third suite.
- New or changed behavior MUST have appropriate integration coverage.
- Test real HTTP, authentication, and `ts-rust` persistence boundaries; do not mock the critical path.
- Use isolated synthetic accounts and clean up test data.
- Use provider sandboxes or controlled test services for external delivery and federation; never send test traffic to real users.

### Fast — Required and Blocking

- Cover critical paths, common user workflows, and essential security invariants.
- Run as the first CI validation gate after the setup/build steps needed to execute tests.
- MUST pass before every push, merge, deployment, release, and downstream delivery action.
- Results MUST cover the current changes or exact commit being advanced.
- Failures MUST block the associated operation.
- Never skip, disable, or weaken tests to bypass a failure.

Cover the implemented primary flows:

- Account creation, verification where required, login, authenticated access, and logout.
- Rejection of invalid credentials, expired/revoked sessions, and unauthorized cross-tenant access.
- Credential recovery and single-use challenge enforcement.
- Token issuance, validation, refresh, and replay rejection where supported.
- OpenAPI validity, Scalar loading, and critical Datastar interactions.

### Slow — Independent and Non-Blocking

- Provide comprehensive coverage of concurrency, recovery, federation, key rotation, dependency failures, and expensive browser scenarios.
- Run in a separate CI workflow/action, independently of the release-critical pipeline.
- MUST NOT block pushes, merges, deployments, or releases.
- MUST NOT be a required status check or a dependency of release jobs.
- Preserve visible failures; non-blocking does not mean passing.
- On failure, use `fj` to create a Forgejo issue or update an existing open report for the same failure.
- Include failed tests, commit SHA, environment, reproduction steps, expected/actual behavior, redacted logs, and the CI run URL.
- Ensure reporting runs even when the test step fails.
- Keep sensitive vulnerability details in the approved private reporting channel, not public issues.

Confirmed security defects MUST be surfaced immediately. Add a minimal regression to `fast` rather than leaving the defect covered only by a non-blocking test.

## Issues and Git

### Issue Required

- Track development work in **`ku.mariogk.com`**.
- Resolve a valid Ku issue at the start of each implementation task.
- Accept a Ku URL, issue ID, or an unambiguously resolved issue title.
- Normalize IDs and verify the match; never invent a project prefix or issue ID.
- Ask for an issue when none is supplied, or clarification when multiple matches remain.
- Never push task changes or open a PR without the corresponding Ku issue.
- Automated `slow` failure reports use Forgejo Issues as an explicit exception; follow-up development still requires a Ku issue.

### Branches and Worktrees

- **Prefer a dedicated Git worktree for each task.** Worktrees are the default, not mandatory.
- Fetch the latest `origin/main` and create the task branch from it.
- Use `feat/`, `fix/`, `hotfix/`, or `refactor/` with a descriptive name.
- Use the existing checkout when explicitly requested or when a worktree is impractical.
- Preserve unrelated changes and keep each task’s commits and PR isolated.
- Never implement task changes directly on or push directly to `main`.
- Remove worktrees only after confirming they contain no uncommitted changes, unpushed commits, or files that must be preserved.

### Tooling

- Use `git` for repository operations.
- Use **`fj`** for Forgejo operations.
- Use Ku tooling for development issues.
- Check available CLI help when syntax is uncertain; do not invent commands.

## Pull Requests

### Required Checks

- Formatting, build, Clippy, required security checks, OpenAPI validation, and `fast` MUST pass before pushing.
- Do not wait for or require `slow`; it runs independently.
- Update documentation and compatibility notes for API, storage, or authentication behavior changes.
- Never claim a check passed unless it actually ran successfully.

### Configuration

- Target **`main`**.
- Use the title format **`{ISSUE_ID}: {title}`**.
- Include the Ku link, change summary, validation performed, and relevant security or migration implications.
- Request reviews from **`mariogk`** and **`terrascale-bot`**, excluding the PR author.
- Enable auto-merge once required checks pass; never bypass checks or required reviews.

### Visual Changes

- Attach useful before/after screenshots covering affected layouts and states.
- Use synthetic data and redact sensitive information.
- Upload screenshots as PR attachments; never commit them solely for PR documentation.

### Completion

- Verify the target branch, title, reviewers, attachments, and current CI status.
- Distinguish required checks from non-blocking `slow` results.
- Send the PR URL and identify unresolved blockers or linked failure reports.

## Official References

Consult the documentation matching the versions actually installed.

- [Rust documentation](https://doc.rust-lang.org/)
- [Cargo manifests and lockfiles](https://doc.rust-lang.org/cargo/guide/cargo-toml-vs-cargo-lock.html)
- [Datastar guide](https://data-star.dev/guide/getting_started)
- [Datastar Rust SDK](https://github.com/starfederation/datastar-rust)
- [Datastar security](https://data-star.dev/reference/security)
- [OpenAPI specification](https://spec.openapis.org/oas/latest.html)
- [Scalar API References](https://scalar.com/products/api-references/getting-started)
- [Scalar configuration](https://scalar.com/products/api-references/configuration)
- [OWASP authentication](https://cheatsheetseries.owasp.org/cheatsheets/Authentication_Cheat_Sheet.html)
- [OWASP password storage](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html)
- [OWASP session management](https://cheatsheetseries.owasp.org/cheatsheets/Session_Management_Cheat_Sheet.html)
- [OWASP authorization](https://cheatsheetseries.owasp.org/cheatsheets/Authorization_Cheat_Sheet.html)
- [OAuth security best practices](https://www.rfc-editor.org/rfc/rfc9700.html)
- [OpenID Connect specification](https://openid.net/specs/openid-connect-core-1_0.html)
