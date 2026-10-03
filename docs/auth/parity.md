# C# port capability tracker

ts-auth now runs as the C# service (epic TS-424). The Rust workspace was
removed in TS-442. Its source and tests remain in Git history before that
change and are the behavioural reference for each port stream. A capability
counts as served only when its C# endpoints and integration tests have landed.
Nothing here implies acceptance against a live TerraBase deployment.

| Capability | Port stream | C# status |
| --- | --- | --- |
| Configuration, production guards, error envelope | TS-426 | Served; fast tests cover required and malformed settings, secret-free diagnostics and the envelope |
| Health, readiness, OpenAPI, Scalar, bundled assets | TS-426 | Served; fast tests cover version, fail-closed readiness, contract completeness, Scalar rendering in Chromium without third-party requests, asset headers. Slow tests cover probe coalescing, timeouts, stuck checks and waiter cancellation |
| Telemetry (OTLP) and GlitchTip error reporting | TS-426 | Served; fast tests assert that no secret reaches GlitchTip or any log provider |
| Security primitives (Argon2id, AES-GCM, JWT/JWK, TOTP, WebAuthn) | TS-427 | Not yet ported |
| TerraBase native-v1 store, uniqueness and single-use atomicity | TS-428 | Not yet ported; blocked on confirming native-v1 CAS semantics |
| Key ring, email, operator instances, rate limiting | TS-431 | Not yet ported |
| Hosted users, sessions, email links, password recovery, management users/keys | TS-432 | Not yet ported |
| OAuth 2.0/OIDC provider on OpenIddict, device flow, guest accounts, CORS | TS-433 | Not yet ported |
| MFA and email passcodes | TS-434 | Not yet ported |
| Passkeys | TS-435 | Not yet ported |
| OIDC, GitHub and SAML federation | TS-436 | Not yet ported |
| Organizations, invitations, domains, roles and authorization checks | TS-437 | Not yet ported |
| SCIM directories, users, groups (including operator repair runbooks) | TS-438 | Not yet ported |
| JWT templates, webhooks, signed audit log | TS-439 | Not yet ported |
| Billing, quota seats, Stripe webhooks | TS-440 | Not yet ported |
| Hosted admin portal (Datastar) | TS-441 | Not yet ported; bundled assets are served |

Persistent uploaded files are not implemented; admin assets and fonts are
embedded in the executable. If persistent files are added, they use RustFS
through S3.
