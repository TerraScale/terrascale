---
name: supabase-auth-to-terrascale
description: >-
  Migrate Supabase Auth users and sign-in to TerraScale end-user authentication:
  inventory the auth features a project uses, export auth.users and
  auth.identities safely, import users with their existing bcrypt password
  hashes, set up social providers, replace auth.uid() and Supabase sessions, map
  user ids, then verify, cut over and roll back. Use when someone says "migrate
  Supabase Auth", "move our Supabase users to TerraScale", "replace Supabase
  login", "import users with passwords" or "move off Supabase auth".
---

# Supabase Auth to TerraScale

Moves the people who sign in to an application from Supabase Auth to the project's own TerraScale sign-in
(an OpenID Connect issuer, hosted sign-in pages and a user directory per project). Authentication is the part
of a migration where a mistake becomes an account takeover, so this skill is deliberately strict: follow the
steps in order, run the inventory before promising anything, and stop at every **STOP** line.

See also: `supabase-to-terrascale` (whole-project plan), `supabase-database-to-terrascale` (schema, data and
row-level security in general), `supabase-client-to-terrascale` (replacing supabase-js calls). This skill owns
users, credentials, sign-in, sessions and the `auth.uid()` part of policies.

## What changes, in one table

| Topic | Supabase Auth | TerraScale |
| --- | --- | --- |
| Sign-in | supabase-js calls (`signInWithPassword`, `signInWithOtp`, `signInWithOAuth`) | Redirect to hosted pages; authorization code with PKCE `S256` ([end-user auth][eua]) |
| Issuer | `https://<ref>.supabase.co/auth/v1` | `https://auth.terrascale.tech/<project ID>` ([end-user auth][eua]) |
| Access token | JWT, `sub` = UUID, default 1 hour ([Supabase JWTs][sb-jwt]) | ES256 JWT, `sub` = ULID, `aud` = your client ID, 5 minutes ([end-user auth][eua]) |
| Session | Refresh token, lifetime per project settings ([Supabase sessions][sb-sessions]) | Rotating refresh token, 5 minutes to 30 days from original sign-in, default 14 days ([end-user auth][eua]) |
| User id | UUID assigned by Supabase | New ULID per imported user; the Supabase UUID goes into `external_id` ([user profiles][profiles]) |
| Passwords | bcrypt in `auth.users.encrypted_password` ([Supabase][sb-bcrypt]) | Import accepts bcrypt and upgrades it to Argon2id at the next sign-in ([user profiles][profiles]) |
| Row access | Postgres RLS with `auth.uid()` ([Supabase RLS][sb-rls]) | TerraBase rules with `auth.uid` and `auth.claims.<name>`, or checks in your backend ([rules][rules]) |

Every feature, its TerraScale equivalent or gap, and the citation is in
[references/feature-mapping.md](references/feature-mapping.md). The export SQL, the import script and the id
mapping are in [references/user-export.md](references/user-export.md).

## Prerequisites

- [ ] Read access to the Supabase database as the `postgres` role through a direct or session-pooler connection
  string ([connecting to Postgres][sb-connect]), supplied privately as `SUPABASE_DB_URL`. Never paste it into chat,
  a command line you share, or a file in the repo.
- [ ] A TerraScale project, its ID in `TERRASCALE_PROJECT`, and a TerraScale sign-in token of a **project admin**
  supplied privately as `TERRASCALE_ACCESS_TOKEN`, the same credential the docs use for the directory export
  ([end-user auth][eua]). The import route also takes an unrestricted project secret API key with `auth:write`
  ([served OpenAPI][openapi], operation `import_project_users`).
- [ ] A second, throwaway TerraScale project for the rehearsal. Never rehearse in the production project:
  imported accounts are real accounts.
- [ ] `psql`, `jq`, Node.js 22 or newer, and a private working directory (`umask 077`) outside every git
  repository, on an encrypted disk. The export holds emails and password hashes.
- [ ] Someone who owns the decision for each gap the inventory finds (phone-only users, MFA, SAML, anonymous users).

## Safety rules (read before step 1)

1. **Never move plaintext passwords.** Nobody has them and nobody should. Only bcrypt hashes move, only over
   HTTPS to the import route, and only for confirmed, non-banned accounts.
2. **Never select secrets to a terminal or a log.** Inventory queries return counts and hash prefixes such as
   `$2a$10$` (algorithm and cost, not secret). Full hashes go only to a `0600` file through `psql -o`. Never
   select `auth.mfa_factors.secret`, `auth.refresh_tokens`, `auth.sessions` tokens, `confirmation_token` or
   `recovery_token`.
3. **Never mark an unconfirmed address verified.** `email_verified` is `true` only when `email_confirmed_at` is
   set. TerraScale keeps the imported value and says administrators must not mark imported unverified addresses
   as verified ([user profiles][profiles]).
4. **No password hash for an unconfirmed address.** TerraScale's password sign-in checks the password, not
   `email_verified` ([source][src-hosted]), so a hash set by someone who never proved the mailbox would become a
   working login. Import those accounts without `password_hash`; they sign in with an email code, which proves
   the mailbox.
5. **Never link accounts by email in your own code** unless both sides proved the mailbox. TerraScale itself
   never links a social identity to an account by email ([end-user auth][eua]).
6. **Turn TerraScale sign-up off until the import has finished**, so no new account takes an address that is
   about to be imported.

## Step 1: Inventory

Run the queries in [references/user-export.md](references/user-export.md#1-inventory) and fill this table. They
read counts only.

| Item | Query result | Why it matters |
| --- | --- | --- |
| Users total, soft-deleted, anonymous, SSO, banned | | Deleted and anonymous users are not migrated; SSO and banned users need their own path |
| Users without an email (phone-only) | | Import needs an email; TerraScale has no phone sign-in |
| Users with a password, by hash prefix | | Only canonical bcrypt with cost 04 to 12 is accepted |
| Users with `email_confirmed_at` null | | No hash, no `email_verified` for them |
| Identities by provider | | Each provider needs a TerraScale equivalent or a plan |
| MFA factors by type and status | | Factors do not migrate; users enroll again |
| SSO and SAML providers | | SAML is not available |
| `raw_app_meta_data` and `raw_user_meta_data` keys | | Decide claims and metadata per key |
| Duplicate emails (case-insensitive) | | TerraScale emails are unique per project |
| Foreign keys to `auth.users`, policies and functions using `auth.*`, triggers on `auth.users` | | Every one of them changes |
| Auth hooks and email templates (dashboard, Authentication) | | No hook equivalent; branding only |
| Redirect URLs with wildcards (dashboard, URL configuration, [Supabase][sb-redirects]) | | TerraScale redirect URIs are exact |

Then search the application code. Each hit is a line to change in step 6:

```sh
rg -n "auth\.(signIn|signUp|signOut|getSession|getUser|onAuthStateChange|refreshSession|updateUser|resetPasswordForEmail|verifyOtp|mfa|admin)" --glob '!node_modules'
rg -n "auth\.uid\(\)|auth\.jwt\(\)|auth\.role\(\)|auth\.users" --glob '*.sql' --glob '!node_modules'
rg -n "SUPABASE_JWT_SECRET|SUPABASE_SERVICE_ROLE_KEY|/auth/v1/|createServerClient|createBrowserClient" --glob '!node_modules'
```

**STOP** and report to the owner before step 2 if any of these is nonzero: phone-only users, SAML providers,
verified MFA factors, anonymous users with data, providers other than email, Google, GitHub, Apple and one
OIDC issuer. Each has a plan in [references/feature-mapping.md](references/feature-mapping.md), and each needs a
decision, not a guess.

## Step 2: Plan the passwords

**Yes, TerraScale imports existing bcrypt hashes.** The exact path:

- `POST https://auth.terrascale.tech/v1/{project}/users/import` with `{"users":[...]}`, 1 to 500 rows per call,
  ten calls per project per minute, no email sent. Each row takes `email`, optional `password_hash`,
  `email_verified` (default `false`), `first_name`, `last_name`, `image_url`, `external_id`,
  `public_metadata`, `private_metadata` and `unsafe_metadata` ([user profiles][profiles], [served OpenAPI][openapi]).
- Accepted bcrypt: canonical `$2a$`, `$2b$` or `$2y$`, cost 04 to 12, 60 characters ([user profiles][profiles],
  [source][src-hashes]). Supabase stores bcrypt in `auth.users.encrypted_password` ([Supabase][sb-bcrypt]).
- After the first successful password sign-in, TerraScale replaces the bcrypt hash with Argon2id under its
  current policy ([user profiles][profiles], [source][src-hosted]). Users notice nothing.
- Valid rows commit even when other rows fail; failures come back as indexed errors (`invalid_request`,
  `user_conflict`). A lost response can leave committed rows: reconcile before you send again
  ([user profiles][profiles]).

Who gets which path:

| Supabase account | Import as | First sign-in at TerraScale |
| --- | --- | --- |
| Confirmed email, bcrypt hash accepted | `password_hash`, `email_verified: true` | Same password |
| Confirmed email, no password (magic link, OTP, social only) | No hash, `email_verified: true` | Email code; social users then connect their provider |
| Unconfirmed email | No hash, `email_verified: false`, or skip it | Email code proves the mailbox |
| Hash refused by the import | Re-send the row without the hash | Password reset from the hosted page |
| Verified MFA factor (`auth.mfa_factors`) | No hash; `email_verified` as confirmed | Email code, then enroll TOTP again; a password alone never signs them in |
| Banned (`banned_until` in the future) | No hash; the import script disables the account right after its batch | Cannot sign in |

When a hash cannot be imported, the options are, with their trade-offs:

| Option | Cost | Risk |
| --- | --- | --- |
| Password reset from the hosted pages, announced by your own email campaign | One action per user; some never return | Phishing look-alikes: link only to your own domain, never send a reset link yourself |
| Email codes as the main method, passwords optional | No password needed at all | Depends on email delivery from TerraScale's sender ([end-user auth][eua]) |
| Social sign-in first | One click for social users | Only Google, GitHub, Apple and one OIDC issuer ([end-user auth][eua]) |
| Lazy migration through your app | Not available: there is no password-verification hook and no API to set a hash after import | None, because it cannot be built on documented routes |

## Step 3: Set up the TerraScale project

Do this in the rehearsal project first, then repeat it in production.

1. **Authentication, OAuth applications**: register each app. Browser, mobile and desktop apps are **public**,
   server apps **confidential**. Register 1 to 10 exact redirect URIs, `https` or loopback `http`, no wildcards
   ([end-user auth][eua]). Supabase wildcard redirect URLs (preview deployments) need fixed hostnames.
2. **Sign-in methods**: turn on **both** email codes and passwords; email codes are the way in for every user
   without a usable hash. Turn **sign-up off** until step 4 is verified ([end-user auth][eua]).
3. **Configuration**: `session_lifetime_seconds` (300 to 2592000, default 1209600) and the `redirect_urls`
   allowlist ([end-user auth][eua]).
4. **Branding**: name, logo, color and `email_message`. Custom HTML templates and your own sender are not
   supported ([end-user auth][eua]).
5. **Social sign-in**, per provider found in step 1: create an OAuth client in your own provider account and
   register `https://auth.terrascale.tech/ui/<project ID>/social/<provider>/callback`, where `<provider>` is
   `google`, `github`, `apple` or `oidc`; then paste the credentials in the dashboard or call
   `PUT /v1/{project}/providers/{provider}` ([end-user auth][eua]). Create **new** clients rather than editing the
   ones Supabase uses, so a rollback finds Supabase untouched. Store client secrets in a secret manager, never in
   the repo.
6. For an enterprise IdP: generic OIDC with the IdP's `https` issuer. SAML is not available
   ([capabilities][cap]).

## Step 4: Export and import the users

Follow [references/user-export.md](references/user-export.md) end to end, first against the rehearsal project:

1. Export the eligible users to `users.ndjson` with the SQL there (deleted, anonymous, email-less and SSO users
   excluded; hashes only for confirmed, non-banned users without a verified MFA factor and with a canonical
   bcrypt hash), plus `fingerprints.csv` for the delta pass.
2. Run the import script. It writes `mapping.csv` (Supabase UUID, TerraScale id) and `import-errors.ndjson`
   (indexes and codes, never hashes), and disables banned users right after each batch.
3. Reconcile every `user_conflict`: an account you imported earlier (same `external_id`) joins the mapping; an
   account someone else created is **never merged automatically**. Review it by hand.
4. Set `custom_claims` and re-check the disables (step 5).

**STOP** if mapped users plus deliberately skipped users differ from the export count. Find the missing rows first.

## Step 5: Metadata, claims and banned users

| Supabase | TerraScale | Notes |
| --- | --- | --- |
| `raw_user_meta_data` (user-editable `user_metadata`) | `unsafe_metadata`; `first_name`, `last_name`, `image_url` from `given_name`, `family_name`, `avatar_url` | The user can change `unsafe_metadata`; never authorize with it ([user profiles][profiles]) |
| `raw_app_meta_data` (server-set `app_metadata`) | `private_metadata` | Backend-only, never in tokens ([user profiles][profiles]) |
| `app_metadata` keys your RLS or JWT reads (`role`, `tenant_id`, `plan`) | `custom_claims` via `PATCH /v1/{project}/users/{user}` | At most 32 members and 1000 bytes; copied into access tokens as `claims`; visible to the user ([user profiles][profiles]) |
| Custom access token hook | `custom_claims`, set by your backend when the value changes | Static per user; reaches tokens at the next refresh ([rules][rules]) |
| `banned_until` in the future | `POST /v1/{project}/users/{user}/disable` | Ends sessions and refresh tokens ([end-user auth][eua]) |

Metadata objects are limited to 8192 bytes and depth 10 each ([user profiles][profiles]); larger rows come back
as `invalid_request` and need trimming.

## Step 6: Change the application

**Sign-in and sessions.**

- Replace every supabase-js auth call with a maintained OIDC library: authorization code, PKCE `S256`,
  `state` and `nonce`, ID token validation against the discovered JWKS ([end-user auth][eua]). The runnable
  sample is `docs-site/examples/oidc-customer-app` ([end-user auth][eua]).
- There is no password or OTP API for your own forms: the project issuer serves only the authorization code
  and refresh token grants, and browser `fetch` to ts-auth is not supported ([capabilities][cap]). Custom
  sign-in forms become a redirect to the hosted page.
- `onAuthStateChange` and `getSession` become your own session: keep tokens server-side or in memory, never in
  `localStorage` ([end-user auth][eua]). Refresh tokens rotate on every use; store the successor and drop the
  predecessor. On `invalid_grant`, start sign-in again ([capabilities][cap]).
- Sign-out: clear your session, then `/oauth/revoke` and `/oauth/logout` from discovery ([end-user auth][eua]).
- `resetPasswordForEmail`, `updateUser({ password })`, MFA enrollment and provider linking move to the hosted
  pages (password reset, account security page) ([end-user auth][eua], [capabilities][cap]).

**APIs that verify tokens.** Replace the Supabase JWT secret or JWKS check with the project issuer: signature
from `<issuer>/.well-known/jwks.json` with ES256, exact `iss`, `aud` equal to your client ID, and expiry
([end-user auth][eua]). Drop checks on Supabase's `role: authenticated` and `aal` claims; read `sub` and
`claims` instead. ID tokens are not API tokens ([capabilities][cap]).

**`auth.uid()` in SQL and RLS.** End users cannot run SQL against TerraBase ([rules][rules]). Pick one per table:

| Supabase pattern | TerraScale |
| --- | --- |
| Browser queries the table, RLS `using (auth.uid() = user_id)` | TerraBase rules: `"read":"row.user_id = auth.uid"`, and `"set":{"create":{"user_id":"auth.uid"}}` instead of a client-supplied owner ([rules][rules]) |
| `auth.jwt() -> 'app_metadata' ->> 'role' = 'admin'` | `auth.claims.role = 'admin'`, with `role` in `custom_claims` ([rules][rules]) |
| `auth.role() = 'authenticated'` | Any end-user token; `"read":"TRUE"` allows every signed-in end user ([rules][rules]) |
| Server code with the service role key | A project API key on the server; rules do not limit it, so your code filters by the verified `sub` ([rules][rules], [authentication][auth]) |
| Backend that keeps its own users | Mint a scoped end-user token (at most 15 minutes) with a key that has `auth:tokens:mint` ([end-user auth][eua]) |

Rules apply to project sign-in access tokens directly: they carry your project's data audience and the user's
`claims` and reach no row until the database's rules allow it ([end-user auth][eua]). The general schema, RLS
and data move is in `supabase-database-to-terrascale`.

**User ids and foreign keys.** Imported users get new ULIDs; the import cannot keep the Supabase UUID as the
id, and `auth.uid` is the token's `sub` ([user profiles][profiles], [rules][rules]). So:

1. Keep `mapping.csv` as a table `migration.auth_user_map (supabase_id, terrascale_id)` in a schema the Data API
   does not expose.
2. For every column found by the foreign-key query, add a text column with the TerraScale id and fill it from
   the map (SQL in [references/user-export.md](references/user-export.md#6-map-user-ids-and-foreign-keys)).
   Additive columns leave Supabase working for a rollback.
3. Drop the `REFERENCES auth.users` constraints in the TerraScale schema: users live in TerraScale
   authentication, not in your database. `ON DELETE CASCADE` becomes your own cleanup when you delete a user.
4. Replace triggers on `auth.users` (the usual `handle_new_user` profile insert) with an upsert keyed by `sub`
   on the user's first authenticated request; TerraScale has no database trigger on sign-up.
5. Keep the old UUID column until the rollback window closes.

## Step 7: Verify (rehearsal project first, then production)

- [ ] Mapped users plus skipped users equals the export count; `import-errors.ndjson` reviewed to the last row.
- [ ] The directory export count matches ([end-user auth][eua]); `email_verified` is `true` only for confirmed
  users; disabled count equals banned count.
- [ ] A seeded test user created in Supabase with a known password before the export signs in at TerraScale
  with that password, then signs in again (now on Argon2id).
- [ ] An unconfirmed test user has no working password and can sign in only with an email code.
- [ ] A test user with a verified Supabase TOTP factor has no working password at TerraScale, signs in with an
  email code and enrolls TOTP again.
- [ ] A test user whose password changes in Supabase after the export is found by the delta pass, re-imported,
  and only the new password works.
- [ ] A Google (or other social) test user signs in with an email code, connects the provider on the security
  page, then signs in with the provider.
- [ ] A user with a refused hash resets the password on the hosted page.
- [ ] A banned test user cannot sign in by any method.
- [ ] Every foreign-key column has zero rows whose owner is missing from the map, or the owner signed off on
  the orphans.
- [ ] TerraBase rules: user A cannot read or write user B's rows; anonymous requests get `401` ([rules][rules]).
- [ ] Your API refuses a Supabase JWT, an ID token, an expired token and a token for another client.
- [ ] No hash, email or token appears in shell history, CI logs, application logs or the repo
  (`rg -n '\$2[aby]\$' .` finds nothing).

## Step 8: Cut over

Users signed in at Supabase cannot carry their session over: the import moves no sessions or tokens
([end-user auth][eua]). Plan the window:

1. **Announce** the date and that everyone signs in once more; social-only users sign in with an email code and
   reconnect their provider; MFA users enroll again.
2. **Reduce Supabase auth writes**: turn off "Allow new users to sign up" ([Supabase][sb-config]) and ship an
   app build that hides password change and email change. This is not a freeze: Supabase's auth API still
   accepts password resets and `updateUser` from old app versions and from anyone holding the anon key. The
   delta pass below is what makes the import current.
3. **Final export and import** into production (step 4), then claims (step 5). At about 5000 users a minute
   (500 rows, ten calls a minute), 100000 users take about 20 minutes plus reconciliation.
4. **Delta pass until zero** ([references/user-export.md](references/user-export.md#7-delta-pass-before-the-switch)):
   find every user whose password hash, email, confirmation, ban, MFA state or `app_metadata` changed since
   their import, or who was deleted or created since; delete and re-import them while no TerraScale data
   exists for them. Repeat until the pass finds nothing. **STOP** and do not switch while it finds anything.
5. **Fill the id columns** again (reloaded map) for rows written since the rehearsal, then move the data
   (`supabase-database-to-terrascale`). Run one more delta pass; it must find zero.
6. **Switch the application** to the TerraScale issuer. From now on a Supabase token is refused and the user is
   sent to the hosted sign-in.
7. **Last delta pass right after the switch**. A user it finds changed in Supabase in the last minutes: disable
   the TerraScale account at once (ends sessions), then support confirms the person, deletes and re-imports the
   account and rewrites that user's few TerraScale rows to the new id.
8. **Stop old clients writing**: mobile apps you cannot force-update still hold Supabase sessions. Revoke the
   Data API roles' writes in Supabase (SQL in [references/user-export.md](references/user-export.md#8-cut-over-and-roll-back)).
9. **Turn TerraScale sign-up on** if the app allows sign-up.
10. Watch sign-in errors, `invalid_grant` rates and support tickets for a week.

The point of no return is the first write to TerraScale by a migrated user. Before it, rollback is a config
switch; after it, rollback needs a reverse data move.

## Step 9: Roll back

- **Before any TerraScale write**: point the app back to Supabase, grant the writes back, turn Supabase sign-up
  on. Nothing in Supabase was deleted or changed except the additive id columns.
- **After TerraScale writes**: freeze TerraScale sign-up, move the data back, then in this order
  ([references/user-export.md](references/user-export.md#8-cut-over-and-roll-back)):
  1. Create each user who signed up at TerraScale (not in `auth_user_map`) in Supabase with the admin API and
     `email_confirm: true` ([Supabase admin createUser][sb-admin]); TerraScale sign-ups proved their address at
     account creation ([end-user auth][eua]).
  2. Insert each new pair (new Supabase UUID, TerraScale id) into `migration.auth_user_map`.
  3. Backfill every `user_id` column from its `user_ts_id` through the map, then check the orphan count is zero.
  4. Ask **every** rolled-back user to reset the password: TerraScale exports no hashes ([end-user auth][eua]),
     so a password changed at TerraScale does not move back, and Supabase still holds the old one.
- **Halfway failure** (import stopped, mapping incomplete): do not switch the app. The import is per-row, so
  reconcile and resume; or delete the TerraScale users you created (`DELETE /v1/{project}/users/{user}`,
  [end-user auth][eua]) and start over. Supabase is the source of truth until step 8.6.
- Keep the Supabase project, its OAuth clients and the old id columns for at least one full session lifetime
  (14 days by default) after the switch.

## Risk review (what a reviewer will ask)

- **Account takeover during migration**: no hash for unconfirmed, banned or MFA-protected accounts (a password
  alone must not open an account that needed a second factor); banned accounts disabled within their import
  batch; a delta pass re-imports anyone whose password, email or MFA changed after the export, so an old leaked
  password does not keep working; `email_verified` copied, never upgraded; sign-up off during import; conflicts reviewed by hand, never merged by email; TerraScale
  refuses a social identity whose email already has an account ([end-user auth][eua]); the hosted reset and
  email-code flows prove the mailbox.
- **Duplicate users**: the export lowercases and trims emails and the duplicate query runs first; TerraScale
  refuses a second account with the same email or `external_id` (`user_conflict`); resubmission after a lost
  response goes through reconciliation.
- **Orphaned users and rows**: the orphan query in the export reference lists rows whose owner was not
  imported (phone-only, anonymous, deleted); the owner decides delete, keep unowned or contact.
- **Email verification state**: copied from `email_confirmed_at`; unverified users must pass an email code.
- **Cutover fails halfway**: Supabase stays authoritative until the app switch; the import is per row and
  resumable; the delta pass must reach zero before the switch; the rehearsal project proves the run first.

## Product gaps

Listed with workarounds in [references/feature-mapping.md](references/feature-mapping.md#product-gaps): no id
preservation on import, no import of linked social identities or MFA factors, no way to set a password hash
after import, no phone, anonymous, magic-link or SAML sign-in for project users, no auth hooks, no custom
sender, exact redirect URIs only, and the import route is documented in the served OpenAPI and repository docs
but not yet on docs.terrascale.tech.

[eua]: https://docs.terrascale.tech/guides/end-user-authentication
[cap]: https://docs.terrascale.tech/guides/authentication-capabilities
[auth]: https://docs.terrascale.tech/guides/authentication
[openapi]: https://auth.terrascale.tech/openapi/v1.json
[profiles]: https://github.com/TerraScale/terrascale/blob/main/docs/auth/user-profiles.md
[rules]: https://github.com/TerraScale/terrascale/blob/main/docs/terrabase/rules.md
[src-hashes]: https://github.com/TerraScale/terrascale/blob/main/src/TerraScale.Authentication.Security/ImportedPasswords.cs
[src-hosted]: https://github.com/TerraScale/terrascale/blob/main/src/TerraScale.Authentication/Features/SignIn/HostedAuth.cs
[sb-bcrypt]: https://supabase.com/docs/guides/platform/migrating-to-supabase/auth0
[sb-jwt]: https://supabase.com/docs/guides/auth/jwts
[sb-sessions]: https://supabase.com/docs/guides/auth/sessions
[sb-rls]: https://supabase.com/docs/guides/database/postgres/row-level-security
[sb-connect]: https://supabase.com/docs/guides/database/connecting-to-postgres
[sb-redirects]: https://supabase.com/docs/guides/auth/redirect-urls
[sb-config]: https://supabase.com/docs/guides/auth/general-configuration
[sb-admin]: https://supabase.com/docs/reference/javascript/auth-admin-createuser
