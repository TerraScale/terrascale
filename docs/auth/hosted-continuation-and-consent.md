# Hosted continuation, consent and resends

MFA throttling returns hosted HTML with a positive, rounded-up `Retry-After`, a UTC retry deadline and a progressive countdown. Only a sealed, currently valid challenge and validated `next` are retained; factor inputs are never reflected. A refusal must happen before factor consumption. The account budget can outlast the five-minute challenge, in which case primary sign-in is required again before retrying the factor.

Device consent resolves the application's current registered display name and the scopes bound to its validated device code. The page lists both human-readable permissions and scope identifiers before explicit Approve/Deny, alongside the account and matching device code. Unknown/revoked clients and unsupported or no-longer-permitted scopes cannot be approved. Existing origin, session and MFA checks remain mandatory; the browser never supplies consent labels or granted scopes.

Device sign-in grants only `openid`, `email`, `profile` and `offline_access`. Resource scopes such as `documents:read` require a separate role-checked token exchange; a device request containing them shows an unavailable-request page with no consent controls and cannot be approved.

Sign-in, sign-up and password-reset code pages provide native in-context resend forms. Resending a pending sign-up retains the original token and password hash; a stale token cannot replace a newer pending sign-up. Expired pending sign-ups require starting again. Existing mail admission paths and request budgets remain authoritative, including for unknown accounts; a refused resend keeps the verification form and continuation. Successful mail requests show a conservative sixty-second resend deadline, and 429 responses show the current remaining server wait. JavaScript adds countdown and duplicate-submission/busy feedback; native forms remain usable without it. Password requirements and errors are associated with their inputs.

No public JSON authentication contract or persisted document format changes. `/ui/{slug}/sign-up/resend` is an additional same-origin form endpoint, documented in OpenAPI. Browser fields remain untrusted and no password is serialized back into HTML.

Release qualification requires required CI and quota-safe own-account proof by the integrating parent. Public HTML inspection alone does not establish live MFA, resend or device-consent correctness.
