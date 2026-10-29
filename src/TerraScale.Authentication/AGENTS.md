# ts-auth

Read the root [AGENTS.md](../../AGENTS.md). Shipping: follow the workspace [Shipping code section](/home/mariogk/Projects/TerraScale/AGENTS.md#shipping-code-user-2026-10-29-go-fast-break-things-fast-q-trunk-q-local-check).

ts-auth is the hosted identity provider. Prioritize security, tenant isolation, correctness and interoperability over convenience or performance. Contract: `docs/auth/platform-contract.md`.

## Ownership and stack

- Own identity/access: users, credentials, sessions, MFA, SSO/federation, OAuth/OIDC clients, signing keys/JWKS, organizations, projects, memberships, roles, invitations, ownership transfers, audits, API client credentials and authentication email branding.
- Keep the platform tenant `terrascale` isolated from each customer project's end-user tenant. Preserve project-bound issuers and all tenant boundaries.
- Persist state only through ts-dotnet (`TerraScale.DotNet`, `src/TerraScale.Authentication.Store`). Never embed/import the TerraBase server or bypass the SDK. Use the TerraBase-issued bootstrap key, never an auth token signed by ts-auth, for its own store. Do not copy other products' data; call their SDKs.
- C#/ASP.NET Core on .NET 11; single-file self-contained Linux x64/arm64 releases. Razor Slices renders HTML; business logic stays out of templates. Datastar drives server updates through `Assets/admin/datastar.js`; no .NET Datastar SDK is currently referenced. Add no other backend runtime or frontend framework.
- Use Geralt/libsodium for Argon2id, Microsoft.IdentityModel for JWTs and OpenIddict for OAuth/OIDC. No custom cryptography or token-signing algorithms.
- Build: `make auth-build`; Fast pre-push: `make auth-test-fast`; Slow: `make auth-test-slow`; checks: `make auth-check`, `make auth-audit`, `make auth-openapi-check`; development: `make auth-dev` (`scripts/auth/dev.sh`).

## Architecture and security

- Verify the SDK's actual consistency, uniqueness and atomicity guarantees. Never replace unavailable persistence with an in-memory store. Instances are stateless; process-local state, locks and server affinity cannot ensure correctness across instances.
- Caches cannot override authorization, expiry or revocation. Document and test staleness limits, revocation and failure handling. Single-use challenges, credential updates, token rotation and identity uniqueness must survive races; no unprotected read-then-write security operations. Missing guarantees require an explicit coordination design, never weaker security.
- Keep signing and encryption material out of public configuration. Generate signing keys and store them encrypted in TerraBase under the master key. Never add an authentication bypass.
- Group code by feature, with boundaries between handlers, application logic, persistence and presentation. Share API/UI authentication and authorization. Keep billing, other product logic and TerraBase internals out.
- Passwords use Argon2id, unique salts and explicit production parameters. Never store plaintext/reversibly encrypted passwords. Use cryptographically secure challenges/tokens. Hash opaque bearer secrets used only for verification; encrypt recoverable ones.
- Recovery, verification and authorization codes expire and are single-use. Reauthenticate sensitive account/credential changes.
- Authenticate and authorize protected operations on the server. Deny by default, grant minimum permissions and fail closed. Validate tenant/client/user/resource relationships; a submitted ID proves nothing. Isolate tenants in queries, caches, tokens, UI and background work.
- Rate-limit auth, recovery, verification and token endpoints. Avoid account enumeration in bodies, status and timing.
- Require production HTTPS. Cookies use Secure, HttpOnly and explicit flow-appropriate SameSite. Protect cookie mutations from CSRF. Use explicit origin/redirect allowlists. Rotate session IDs after authentication/privilege changes; enforce expiry and revocation server-side.
- Never store bearer credentials in browser storage or expose server secrets in HTML, signals or bundles. Never log passwords, tokens, auth headers, cookies or recovery secrets.
- Follow OAuth/OIDC specifications and current guidance. Interactive authorization uses Authorization Code with PKCE; no implicit or password grants. Validate registered redirects and client/PKCE/state/nonce bindings. Trust explicit issuers, never untrusted headers. Validate signature, algorithm, issuer, audience, expiry and intended use. ID tokens are not API access tokens. Prevent refresh replay; document rotation, expiry and revocation. Rotate signing keys safely. Prove interoperability with an independent client.

## Contracts and UI

- One canonical OpenAPI document covers implemented methods, paths, parameters, bodies, responses, errors, authentication and permissions. Use unique stable operation IDs and shared/generated schemas. Security declarations match enforcement.
- Change implementation, OpenAPI, examples and integration tests together. Preserve standards-required content types and errors. Scalar loads the same contract checked by `make auth-openapi-check`.
- Examples use synthetic data. Document internal/admin APIs without publishing them. Never preload credentials, persist documentation authorization or send authenticated doc requests through third-party proxies. Self-host pinned documentation assets.
- Fast validates OpenAPI structure/references, routes, representative responses and Scalar loading without browser errors.
- Render on the backend; use Datastar for incremental interaction, SSE when appropriate. Keep identity and permissions server-side. Browser signals are untrusted; send only necessary fields. Escape values and keep untrusted input out of executable expressions. Never echo credentials into HTML or streams.
- Keep CSP-compatible configuration and security headers. Provide accessible forms, keyboard navigation, visible focus and clear validation. Preserve behavior across repeated updates, reconnects and navigation. Reuse brand assets.

## Tests

- Integration/end-to-end suites: `tests/TerraScale.Authentication.Fast` and `.Slow`; shared source utilities in `.TestHarness`. Cover changed behavior through real HTTP, authentication and ts-dotnet persistence boundaries; do not mock critical paths. Use isolated synthetic accounts and clean up. Delivery/federation use sandboxes or controlled services, never real users. Browser tests use Playwright Chromium.
- Fast covers signup/verification, login/access/logout, invalid credentials, expired/revoked sessions, cross-tenant denial, recovery/single-use challenges, token issuance/validation/refresh/replay, OpenAPI/Scalar and critical Datastar interactions.
- Slow covers concurrency, recovery, federation, key rotation, dependency failures and expensive browser scenarios independently.
- Surface confirmed security defects immediately and add a minimal Fast regression.
- Consult installed-version documentation for .NET, ASP.NET Core, OpenIddict, Datastar/CSP, OpenAPI, Scalar, OWASP auth/password/session/authorization guidance, RFC 9700 and OpenID Connect Core.
