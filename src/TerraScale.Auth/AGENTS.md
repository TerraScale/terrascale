# ts-auth Agent Notes

> [!IMPORTANT]
> **AI agents MUST NOT edit this file. Only humans may modify it.**

Read [`AGENTS.md`](../../AGENTS.md) at the repository root first. Its rules apply here. This file adds the ts-auth-only notes.

## Project

`ts-auth` is the hosted identity provider for the TerraScale platform, in the same product category as Clerk, Auth0, and WorkOS.

It is built on **TerraBase through the ts-dotnet SDK** (`TerraScale.DotNet`, referenced from `src/TerraScale.Auth.Store`). The service was ported from Rust to C# (see the port status in `README.md`); the Rust code remains in Git history only.

Prioritize security, tenant isolation, correctness, and interoperability over convenience or performance shortcuts.

## Service Ownership

User decision 2026-10-06 (epic TS-597; workspace rules, "Service ownership"). The contract is `docs/auth/platform-contract.md`.

ts-auth is the single owner of identity and access:

- Tenants: `terrascale` for TerraScale customers, plus one tenant (issuer `https://<region>.auth.terrascale.tech/<project id>`) per customer project for its end users, strictly isolated.
- Users, credentials, sessions, MFA, SSO and federation, OAuth/OIDC clients and every token ts-auth signs, with their signing keys and JWKS.
- Organizations, projects (organization → project), organization and project memberships, roles, invitations, ownership transfer and their audit events.
- API keys: client credentials owned by an organization or project, exchanged at the token endpoint for short-lived JWTs.
- Authentication emails and their branding.

ts-auth does not own, and must not store copies of, other products' data. It reaches them only through the TerraScale SDKs:

- **TerraBase** (through ts-dotnet): databases, collections, documents, backups, indexes, schema and usage metering. ts-auth persists its own state there with a TerraBase-issued bootstrap key, never with a token it signed itself.
- **ts-dashboard**: the platform UI, platform API and MCP server, plans, subscriptions, Stripe billing and spending caps.
- **ts-orchestrator**: regions, hosts, releases and placement. **ts-atlas**: region discovery and the global proxies.

TerraBase and the other products authorize from ts-auth tokens offline. ts-auth exposes identity through its documented APIs and tokens and never reaches into another product's storage.

## Stack

The stack is C#. The rules below replace the earlier Rust-era wording with the stack the code actually uses.

- **.NET:** the service is C# on ASP.NET Core. The target framework and SDK are set by the root .NET rules (`.NET 11`). The current pin is .NET 10 (`global.json`, `Directory.Build.props`); moving to .NET 11 is a separate change.
- **Single-file release:** the service ships as a self-contained single-file executable (`linux-x64`, `linux-arm64`), per `Directory.Build.props`.
- **TerraBase access:** all access MUST go through the ts-dotnet SDK (`TerraScale.DotNet`). Do not import or embed the TerraBase server project or bypass the SDK with direct storage access.
- **Server-rendered HTML:** pages and fragments are Razor Slices (`RazorSlices`). Keep business logic out of templates.
- **Datastar:** use Datastar for server-driven interactions and incremental updates. The browser script ships as an embedded asset (`src/TerraScale.Auth/Assets/admin/datastar.js`). Backend Datastar integration uses a Datastar SDK for .NET; none is referenced in `Directory.Packages.props` today.
- HTML, CSS, and minimal JavaScript are allowed where needed for browser behavior and tooling. Do not introduce another backend runtime or a competing frontend framework.
- **OpenAPI:** `Microsoft.AspNetCore.OpenApi` with `Scalar.AspNetCore`. `make openapi-check` lints the served document with Redocly.
- **Cryptography:** Argon2id through Geralt (libsodium), JWTs through `Microsoft.IdentityModel`, and the OAuth/OIDC server through OpenIddict. Do not implement custom cryptography or token-signing algorithms.
- **Build checks:** `make check` runs formatting, the build with analyzers as errors, the NuGet vulnerability audit, the OpenAPI lint and the Fast suite.

## Architecture

### Storage and State

- All TerraBase access MUST go through the ts-dotnet SDK.
- Persistent identity and authentication state MUST live in TerraBase.
- Keep service instances stateless. Do not require server affinity or process-local state for correctness.
- Caches MAY improve performance but MUST NOT override authorization, expiry, or revocation requirements.
- Keep signing keys and encryption secrets in the approved secret-management system (OCI Vault in production), separate from source code and public configuration.
- Never silently substitute in-memory persistence when the database is unavailable.

### Zero configuration (ts-auth)

- Discover regional service endpoints automatically. Signing, delivery and service credentials are created and configured through OCI Vault without an authentication bypass.

### Consistency

- Verify the actual consistency, uniqueness, and atomic-operation guarantees available through ts-dotnet.
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
- Store hashes of opaque bearer secrets when only verification is required. Encrypt secrets that must be recoverable.
- Verification, recovery, and authorization codes MUST expire and be single-use.
- Require appropriate reauthentication before sensitive credential or account changes.

### Access Control

- Authenticate and authorize every protected operation on the server.
- Deny access by default and grant only the permissions required.
- Validate tenant, client, user, and resource relationships. Never trust a submitted identifier as proof of access.
- Keep tenants isolated across database queries, caches, tokens, UI responses, and background operations.
- Rate-limit authentication, recovery, verification, and token endpoints.
- Avoid account-enumeration leaks through response bodies, status codes, or obvious timing differences.
- Fail closed when required authentication or authorization checks cannot be completed.

### Sessions and Browser Security

- Require HTTPS in production.
- Use `Secure`, `HttpOnly` session cookies with an explicit `SameSite` policy appropriate for supported authentication flows.
- Protect cookie-authenticated mutations against CSRF.
- Use explicit origin and redirect allowlists. Never allow arbitrary redirects.
- Rotate session identifiers after authentication and privilege changes.
- Enforce expiry and documented logout/revocation behavior server-side.
- Do not persist bearer credentials in browser storage or expose server secrets in HTML, Datastar signals, or client bundles.
- Never log passwords, tokens, authorization headers, session cookies, or recovery secrets.

### Authentication Protocols

When implementing OAuth or OpenID Connect:

- Follow the applicable specifications and current security guidance rather than inventing equivalent-looking protocols.
- Use Authorization Code with PKCE for interactive client authorization. Do not implement implicit or resource-owner-password grants.
- Validate registered redirect URIs and enforce the required client, PKCE, state, and nonce bindings.
- Explicitly configure trusted issuers. Do not derive them from untrusted request headers.
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
- Prefer shared C# types and generated schemas over separately maintained definitions.
- Update implementation, OpenAPI, examples, and integration tests in the same change.
- Preserve standards-required content types and error formats. Do not force every endpoint into a custom JSON convention.
- OpenAPI security declarations MUST correspond to actual server-side enforcement.

### Documentation

- Serve the OpenAPI document at `/openapi.json` and Scalar at `/docs`, unless the repository already establishes different paths.
- Scalar MUST load the same contract validated by CI.
- Keep examples realistic but synthetic. Never include real credentials or customer data.
- Document internal and administrative APIs without exposing restricted documentation publicly.
- Do not preload production credentials, persist authorization secrets, or route authenticated documentation requests through third-party proxies.
- Self-host documentation assets where practical and resolve their versions reproducibly.

### Validation

- Validate OpenAPI structure and references in CI.
- Check implemented routes and representative responses against the contract.
- Test that Scalar loads the schema without browser errors.
- Include API contract and documentation smoke checks in Fast.

## Datastar UI

- Render pages and HTML fragments on the .NET backend.
- Use Datastar for interactions and incremental updates. Use SSE only where appropriate.
- Keep authoritative identity state and permission decisions on the server.
- Treat browser signals as untrusted input. Send only the fields required for each action.
- Escape rendered values and keep untrusted data out of executable Datastar expressions.
- Never echo passwords or authentication secrets into rendered HTML or update streams.
- Use the documented CSP-compatible configuration. Do not weaken security headers merely to make an interaction work.
- Provide accessible forms, keyboard navigation, visible focus states, and usable validation messages.
- Preserve behavior after repeated updates, reconnects, and navigation.
- Reuse committed brand assets rather than duplicating logo markup.

## Development and Previews

### Builds

- Use the ts-auth Makefile targets (`auth-*` at the root) and `scripts/auth/dev.sh` for development servers.
- Keep formatting, build, analyzers, and dependency-security checks passing.
- Do not suppress warnings or security findings without a documented justification.
- Keep development credentials, signing keys, and data separate from production.

### Previews

- Development servers MUST be accessible through the intended Tailscale network.
- Enable automatic rebuilds and browser reload. Use hot reload where supported.
- Assign each active worktree its own available application and reload ports.
- Never stop another worktree's processes or reuse occupied ports.
- Use HTTPS for previews that exercise secure cookies or authentication redirects.
- Register exact development callback URLs. Do not weaken production redirect validation for previews.

When a preview is requested:

1. Start the environment for the correct branch and worktree.
2. Verify the actual Tailscale DNS URL, including HTTPS and the required port.
3. Test the changed functionality, authentication redirects, cookies, and reload behavior.
4. Fix failures before sharing the URL.
5. Send the verified preview URL to the user.

Never invent a hostname, send localhost as a remote preview, or present an unverified environment as working.

## Testing

### Structure

- Suites: `tests/TerraScale.Auth.Fast` and `tests/TerraScale.Auth.Slow`. Shared utilities live in `tests/TerraScale.Auth.TestHarness`.
- Integration and end-to-end tests only. Do not create unit tests.
- New or changed behavior MUST have appropriate integration coverage.
- Test real HTTP, authentication, and ts-dotnet persistence boundaries. Do not mock the critical path.
- Use isolated synthetic accounts and clean up test data.
- Use provider sandboxes or controlled test services for external delivery and federation. Never send test traffic to real users.
- Browser tests use Playwright (Chromium).

### Fast: required and blocking

- Cover critical paths, common user workflows, and essential security invariants.
- Run as the first CI validation gate after the setup and build steps needed to execute tests.

Cover the implemented primary flows:

- Account creation, verification where required, login, authenticated access, and logout.
- Rejection of invalid credentials, expired or revoked sessions, and unauthorized cross-tenant access.
- Credential recovery and single-use challenge enforcement.
- Token issuance, validation, refresh, and replay rejection where supported.
- OpenAPI validity, Scalar loading, and critical Datastar interactions.

### Slow: independent and non-blocking

- Provide comprehensive coverage of concurrency, recovery, federation, key rotation, dependency failures, and expensive browser scenarios.
- Run in a separate CI workflow, independently of the release-critical pipeline.

Confirmed security defects MUST be surfaced immediately. Add a minimal regression to Fast rather than leaving the defect covered only by a non-blocking test.

## Pull Requests

- Attach useful before/after screenshots covering affected layouts and states for visual changes.
- Use synthetic data and redact sensitive information.
- Upload screenshots as PR attachments. Never commit them solely for PR documentation.

## Official References

Consult the documentation matching the versions actually installed.

- [.NET documentation](https://learn.microsoft.com/dotnet/)
- [ASP.NET Core documentation](https://learn.microsoft.com/aspnet/core/)
- [OpenIddict documentation](https://documentation.openiddict.com/)
- [Datastar guide](https://data-star.dev/guide/getting_started)
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
