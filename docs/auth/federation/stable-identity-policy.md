# Platform federated identity foundation

This contract covers platform Google and GitHub sign-in only. It supersedes older
email-autolinking descriptions for those platform flows. Project social and
organization OIDC SSO still use their existing email resolution policy; extending
stable bindings to them is separate work. SAML sign-in is not implemented.

A tenant-local binding contains exact, case-sensitive provider, authority, client
ID and subject values. Its claim key is a source-generated JSON object, scoped by
tenant, rather than delimiter concatenation. Google uses the fixed authority
`https://accounts.google.com` and the subject from its trusted UserInfo backchannel.
GitHub uses `https://github.com` as an OAuth authority namespace (not an OIDC issuer
assertion) and the numeric `/user` ID fetched with the same token as verified primary
email. Neither authority is accepted from browser input or arbitrary token claims.

A known binding selects the same enabled local ULID even when upstream verified
email changes or belongs to another local account. Sign-in leaves local email,
its unique claim, recovery destination, credentials and authentication epoch/stamp
unchanged. Local email changes require a separate verified account action.

For an unknown subject and unused verified email, one TerraBase transaction creates
the ULID account, email claim and provider claim. A taken email refuses sign-in
without attaching a credential, merging accounts or replacing a bound subject.
The generic failure page advises retrying from the application or using an existing
sign-in method, without identifying the conflicting account. Existing-account
linking requires deliberate local-account proof; that ceremony is not implemented
in this slice. An existing or unlinked account must use its existing sign-in method.

Browser/state/tenant/client/secret binding, durable callback consumption and session
credential ancestry remain enforced. When the bound account has an active
authenticator app, the callback opens no session: it answers the hosted second-factor
page with a sealed challenge, exactly like password or email-code sign-in. That
challenge carries the account epoch/stamp and the consumed callback binding, lasts 5
minutes and completes once with a TOTP step or recovery code. Unlinking the provider,
a sign-out everywhere or a factor change kills it. Upstream `amr`/`acr` never count
as a second factor. An unknown subject never reaches the factor step: an email
collision is refused before any challenge, so the incoming email cannot choose the
account whose factor is asked for.

This is an explicit greenfield auth storage change: persisted links now require an
authority and provider claim keys use structured JSON. Old links/claim keys are not
converted, inferred from email or read through a compatibility shim. Old records can
prevent provider sign-in/unlink under the new key; the deployment owner must arrange
appropriate clean alpha auth state before rollout. This patch performs no reset,
migration or TerraBase storage-format bump. Client replacement selects a different
identity namespace; secret rotation retains it and invalidates pending old-secret
callbacks through the existing credential hash check.

No public HTTP/SDK shape changes: platform provider responses remain names only;
project exports retain their explicit DTO mapping without the new authority field.
Fast HTTP regressions cover changed email, collisions, case-sensitive subject
replacement refusal, namespace separation, collision refusal with an active factor,
bound-account TOTP completion through the OAuth code flow, single-use challenges and account-state
preservation using real TerraBase storage and controlled provider transports.
Execution evidence belongs to CI; these cases do not establish live-provider or
multi-region race/convergence qualification. Those checks, project/SSO bindings
and their MFA completion, and deliberate linking remain follow-up work.
