# ts-auth Agent Notes

> [!IMPORTANT]
> **AI agents MUST NOT edit this file. Only humans may modify it.**

Read [`AGENTS.md`](../../AGENTS.md) at the repository root first. Its rules apply here.

## Project

`ts-auth` is the hosted identity provider for the TerraScale platform (like Clerk, Auth0 and WorkOS). It is built on **TerraBase through the ts-dotnet SDK** (`TerraScale.DotNet`, via `src/TerraScale.Authentication.Store`). It was ported from Rust to C#; Rust remains in Git history only (see `README.md`).

Prioritize security, tenant isolation, correctness and interoperability over convenience or performance.

## Service ownership

User decision 2026-10-06. Contract: `docs/auth/platform-contract.md`.

ts-auth is the single owner of identity and access:

- Tenants: `terrascale` for TerraScale customers, plus one strictly isolated tenant per customer project (issuer `https://<region>.auth.terrascale.tech/<project id>`) for its end users.
- Users, credentials, sessions, MFA, SSO and federation, OAuth/OIDC clients, and every token ts-auth signs (with its signing keys and JWKS).
- Organizations, projects (organization → project), memberships, roles, invitations, ownership transfer and audit events.
- API keys: client credentials owned by an organization or project, exchanged at the token endpoint for short-lived JWTs.
- Authentication emails and their branding.

ts-auth never stores copies of other products' data. It reaches them only through the TerraScale SDKs:

- **TerraBase** (ts-dotnet): databases, collections, documents, backups, indexes, schema and usage metering. ts-auth persists its own state there with a TerraBase-issued bootstrap key, never with a token it signed itself.
- **ts-dashboard**: platform UI, API and MCP server, plans, subscriptions, Stripe billing and spending caps.

ts-auth runs on OCI under ts-orch, which owns regions, placement, scaling, edge, TLS and secrets (OCI Vault). TerraBase and other products authorize from ts-auth tokens offline. ts-auth never reaches into another product's storage.

## Stack

- **.NET:** C# on ASP.NET Core, per the root .NET rules (.NET 11). The current pin is .NET 10 (`global.json`, `Directory.Build.props`); moving to .NET 11 is a separate change.
- **Release:** self-contained single-file executables (`linux-x64`, `linux-arm64`), per `Directory.Build.props`.
- **TerraBase access:** all access MUST go through the ts-dotnet SDK (`TerraScale.DotNet`). Never import or embed the TerraBase server project or bypass the SDK.
- **HTML:** Razor Slices (`RazorSlices`); keep business logic out of templates. HTML, CSS and minimal JavaScript are allowed for browser behavior and tooling. Add no other backend runtime or frontend framework.
- **Datastar:** server-driven updates. The browser script is `src/TerraScale.Authentication/Assets/admin/datastar.js`. No Datastar SDK for .NET is referenced in `Directory.Packages.props` today.
- **OpenAPI:** `Microsoft.AspNetCore.OpenApi` with `Scalar.AspNetCore`. `make openapi-check` lints the served document with Redocly.
- **Cryptography:** Argon2id through Geralt (libsodium), JWTs through `Microsoft.IdentityModel`, OAuth/OIDC through OpenIddict. Do not implement custom cryptography or token-signing algorithms.
- **Build checks:** `make check` runs formatting, the build with analyzers as errors, the NuGet vulnerability audit, the OpenAPI lint and the Fast suite.

## Architecture

- **State:** persistent identity and authentication state MUST live in TerraBase, accessed only through ts-dotnet. Verify the consistency, uniqueness and atomic guarantees ts-dotnet actually provides. Never silently substitute in-memory persistence when the database is unavailable.
- **Stateless instances:** do not require server affinity or process-local state for correctness. Process-local locks MUST NOT count as coordination between instances.
- **Caches** MAY improve performance but MUST NOT override authorization, expiry or revocation.
- **Secrets:** keep signing keys and encryption secrets in the approved secret manager (OCI Vault in production), separate from source and public configuration.
- **Zero configuration:** ts-orch supplies regional identity and endpoints (`TS_REGION`, `TS_NODE`, `TS_PEERS_FILE`); no per-region setting. Delivery and service credentials arrive from OCI Vault through ts-orch. ts-auth generates its signing keys and stores them encrypted in TerraBase under an OCI Vault master key. No authentication bypass.
- **Concurrency:** single-use challenges, credential updates, refresh-token rotation and identity uniqueness MUST stay correct under concurrent requests. Never implement a security-critical operation as an unprotected read followed by a write.
- **Missing guarantees:** if required storage guarantees are unavailable, report the limitation and require an approved coordination design. Never weaken the security requirement silently. Document and test revocation behavior, cache staleness limits and failure handling.
- **Organization:** group code by feature, with clear boundaries between HTTP handlers, application logic, persistence and presentation. Share authentication and authorization logic between APIs and UI flows. Keep billing, product logic and TerraBase internals out of this service. Add abstractions only for a real boundary or meaningful duplication removal.

## Identity and security

- **Credentials:** use established cryptographic libraries. Hash passwords with **Argon2id**, unique salts and explicitly configured parameters appropriate for production. Never store plaintext or reversibly encrypted passwords. Generate tokens and challenges from a cryptographically secure source. Store hashes of opaque bearer secrets when only verification is needed; encrypt secrets that must be recoverable.
- **Lifetime:** verification, recovery and authorization codes MUST expire and be single-use. Require reauthentication before sensitive credential or account changes.
- **Authorization:** authenticate and authorize every protected operation on the server. Deny by default, grant only required permissions, and fail closed when a check cannot be completed. Validate tenant, client, user and resource relationships; never trust a submitted identifier as proof of access. Keep tenants isolated in queries, caches, tokens, UI responses and background work.
- **Abuse:** rate-limit authentication, recovery, verification and token endpoints. Avoid account-enumeration leaks through response bodies, status codes or obvious timing differences.
- **Sessions:** require HTTPS in production. Use `Secure`, `HttpOnly` cookies with an explicit `SameSite` policy suited to the supported flows, and protect cookie-authenticated mutations against CSRF. Use explicit origin and redirect allowlists; never allow arbitrary redirects. Rotate session IDs after authentication and privilege changes. Enforce expiry and logout/revocation server-side.
- **Browser exposure:** never persist bearer credentials in browser storage or expose server secrets in HTML, Datastar signals or client bundles. Never log passwords, tokens, authorization headers, session cookies or recovery secrets.
- **OAuth/OIDC:** follow the applicable specifications and current security guidance; do not invent look-alike protocols. Use Authorization Code with PKCE for interactive authorization. Never implement implicit or resource-owner-password grants. Validate registered redirect URIs and enforce client, PKCE, state and nonce bindings. Configure trusted issuers explicitly, never from untrusted headers. Validate signatures, allowed algorithms, issuer, audience, expiry and intended token use. Never accept an ID token as an API access token. Protect refresh tokens against replay and document rotation, expiry and revocation. Support safe signing-key rotation without exposing private keys. Test supported flows with an independent client before claiming interoperability.

## APIs: OpenAPI and Scalar

- Keep one canonical OpenAPI document covering every implemented operation: methods, paths, parameters, bodies, responses, errors, authentication requirements and permissions. Use stable, unique operation IDs and shared C# types or generated schemas. OpenAPI security declarations MUST correspond to actual server-side enforcement.
- Update implementation, OpenAPI, examples and integration tests in the same change. Preserve standards-required content types and error formats. Do not force every endpoint into a custom JSON convention.
- Serve OpenAPI at `/openapi.json` and Scalar at `/docs`, unless the repository establishes other paths. Scalar MUST load the same contract CI validates.
- Examples are realistic but synthetic, with no real credentials or customer data. Document internal and administrative APIs without publishing them. Never preload production credentials, persist authorization secrets or send authenticated documentation requests through third-party proxies. Self-host documentation assets with reproducible versions.
- CI validates OpenAPI structure and references, checks routes and representative responses against the contract, and confirms Scalar loads without browser errors. These smoke checks belong in Fast.

## Datastar UI

- Render on the .NET backend. Use Datastar for interactions and incremental updates; use SSE only where appropriate.
- Keep identity state and permission decisions on the server. Treat browser signals as untrusted and send only the fields each action needs.
- Escape rendered values and keep untrusted data out of executable Datastar expressions. Never echo passwords or authentication secrets into HTML or update streams.
- Use the documented CSP-compatible configuration. Never weaken security headers to make an interaction work.
- Provide accessible forms, keyboard navigation, visible focus and usable validation messages. Preserve behavior after repeated updates, reconnects and navigation. Reuse committed brand assets rather than duplicating logo markup.

## Development and previews

- Use the `auth-*` Makefile targets and `scripts/auth/dev.sh` for development servers. Keep formatting, build, analyzers and dependency-security checks passing. Do not suppress warnings or security findings without a documented justification. Keep development credentials, signing keys and data separate from production.
- Development servers MUST be accessible through the intended Tailscale network, with automatic rebuilds and browser reload (hot reload where supported). Give each worktree its own application and reload ports. Never stop another worktree's processes or reuse occupied ports.
- Use HTTPS for previews that exercise secure cookies or redirects. Register exact development callback URLs; never weaken production redirect validation for previews.

When a preview is requested:

1. Start the environment for the correct branch and worktree.
2. Verify the real Tailscale DNS URL, with HTTPS and the required port.
3. Test the changed functionality, auth redirects, cookies and reload behavior.
4. Fix failures, then send the verified URL.

Never invent a hostname, send localhost as a remote preview, or present an unverified environment as working.

## Testing

- Suites: `tests/TerraScale.Authentication.Fast` and `tests/TerraScale.Authentication.Slow`; shared utilities in `tests/TerraScale.Authentication.TestHarness`. Integration and end-to-end tests only (root Testing rules). New or changed behavior MUST have appropriate integration coverage.
- Test real HTTP, authentication and ts-dotnet persistence boundaries; do not mock the critical path. Use isolated synthetic accounts and clean up test data. Use provider sandboxes or controlled services for external delivery and federation, never real users. Browser tests use Playwright (Chromium).

### Fast: required and blocking

- Cover critical paths, common workflows and essential security invariants. Run first in CI, after the setup and build steps needed to run tests. Include:
  - Account creation, verification where required, login, authenticated access and logout.
  - Rejection of invalid credentials, expired or revoked sessions, and unauthorized cross-tenant access.
  - Credential recovery and single-use challenge enforcement.
  - Token issuance, validation, refresh and replay rejection where supported.
  - OpenAPI validity, Scalar loading and critical Datastar interactions.

### Slow: independent and non-blocking

- Comprehensive coverage of concurrency, recovery, federation, key rotation, dependency failures and expensive browser scenarios, in a separate CI workflow.

Confirmed security defects MUST be surfaced immediately. Add a minimal regression to Fast rather than leaving the defect covered only by a non-blocking test.

## Pull requests

- The PR previews rule in the root `AGENTS.md` applies to every change to the ts-auth hosted pages (sign-in, sign-up, consent and account pages).

## Official references

Consult the documentation matching the versions actually installed.

- Platform: [.NET](https://learn.microsoft.com/dotnet/), [ASP.NET Core](https://learn.microsoft.com/aspnet/core/), [OpenIddict](https://documentation.openiddict.com/), [Datastar guide](https://data-star.dev/guide/getting_started), [Datastar security](https://data-star.dev/reference/security)
- API: [OpenAPI specification](https://spec.openapis.org/oas/latest.html), [Scalar API References](https://scalar.com/products/api-references/getting-started), [Scalar configuration](https://scalar.com/products/api-references/configuration)
- Security: [OWASP authentication](https://cheatsheetseries.owasp.org/cheatsheets/Authentication_Cheat_Sheet.html), [OWASP password storage](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html), [OWASP session management](https://cheatsheetseries.owasp.org/cheatsheets/Session_Management_Cheat_Sheet.html), [OWASP authorization](https://cheatsheetseries.owasp.org/cheatsheets/Authorization_Cheat_Sheet.html), [OAuth security best practices](https://www.rfc-editor.org/rfc/rfc9700.html), [OpenID Connect specification](https://openid.net/specs/openid-connect-core-1_0.html)
