---
name: firebase-auth-to-terrascale
description: Migrate Firebase Authentication or Identity Platform users, sign-in providers, custom claims, ID-token checks and security rules to TerraScale end-user authentication. Use when you move off Firebase Auth, export Firebase users with auth:export, replace onAuthStateChanged or verifyIdToken, plan a Firebase password or scrypt hash migration, or cut signed-in users over to TerraScale sign-in.
---

# Migrate Firebase Authentication to TerraScale

Use this skill to move the people who sign in to a Firebase app (Firebase
Authentication or Google Cloud Identity Platform) to the project sign-in that
every TerraScale project has: an OpenID Connect issuer, hosted sign-in pages and
a user directory per project
([end-user authentication](https://docs.terrascale.tech/guides/end-user-authentication)).

The result is an inventory, a decided plan, an imported user directory with a
mapping from every Firebase uid, rewritten app and backend code, rewritten data
rules, a verified cutover and a tested rollback.

Read [feature mapping](references/feature-mapping.md) for each Firebase feature
and its TerraScale equivalent or gap, and [user export](references/user-export.md)
for the export, transform, import and PII handling.

See also: firebase-to-terrascale (the Firebase entry point) and
firestore-to-terrascale (moving Firestore data and rules).

## Answer this first: passwords do not move

TerraScale **cannot import Firebase password hashes**. Its user import accepts
only bcrypt, Argon2id PHC and Django PBKDF2-SHA256 hashes
([user profiles and imports](https://github.com/TerraScale/terrascale/blob/main/docs/auth/user-profiles.md#import);
validated in [ImportedPasswords.cs](https://github.com/TerraScale/terrascale/blob/main/src/TerraScale.Authentication.Security/ImportedPasswords.cs)).
Firebase stores passwords with its own modified scrypt
([Firebase CLI auth reference](https://firebase.google.com/docs/cli/auth)), and a
one-way hash cannot be converted into another algorithm. The public docs describe
no sign-in hook that would let TerraScale check a password against Firebase.

Never move plaintext passwords and never build a form that collects them to
re-hash. Choose one of these, or combine them (details in step 4):

| Option | How it works | Trade-off |
| --- | --- | --- |
| Email code first sign-in (recommended) | Import users without a hash, keep email codes on. Users sign in with an emailed code and set a password with the hosted reset if they want one. | One extra step for every password user; needs a reachable mailbox. |
| Password reset campaign | Import without a hash, email users before cutover to use "forgot password" on the hosted page. | Same mailbox need; users who ignore the mail reset at their next visit. |
| Social or passwordless first | Promote Google, GitHub, Apple or OIDC sign-in, plus email codes, and stop offering passwords. | Social users must connect their provider once (step 4); users without email cannot move. |
| Lazy migration on first sign-in | Not offered: the docs describe no TerraScale hook for it, and a do-it-yourself bridge would route plaintext passwords through your servers. | Rejected by this skill. |

## When to use

- The app calls the Firebase Auth client SDK (`signInWithEmailAndPassword`,
  `signInWithPopup`, `onAuthStateChanged`, `getIdToken`) or the Admin SDK
  (`verifyIdToken`, `setCustomUserClaims`, `listUsers`).
- Firestore, Realtime Database or Cloud Storage security rules read `request.auth`.
- You need a plan for signed-in users, social accounts and rollback.

Not covered: moving Firestore documents (firestore-to-terrascale) and TerraScale
platform accounts or organization single sign-on, which serve your team, not your
users ([single sign-on](https://docs.terrascale.tech/guides/single-sign-on)).

## Prerequisites

- [ ] Firebase: Owner or an IAM role that can export users and read the
  **Password hash parameters** in the console, the Firebase CLI logged in, and
  the Admin SDK available to a server script
  ([Firebase CLI](https://firebase.google.com/docs/cli),
  [Admin SDK user management](https://firebase.google.com/docs/auth/admin/manage-users)).
- [ ] TerraScale: a project, the project admin role, and your TerraScale sign-in
  token in a secret manager as `TERRASCALE_ACCESS_TOKEN`. Changing OAuth clients
  needs a fresh step-up proof
  ([sensitive actions](https://docs.terrascale.tech/guides/sensitive-actions)).
- [ ] A restricted, encrypted workspace for the export: it holds emails, phone
  numbers, names and password hashes ([user export](references/user-export.md#pii-and-secret-handling)).
- [ ] Read the current [authentication capabilities](https://docs.terrascale.tech/guides/authentication-capabilities);
  it takes precedence over older lists.

## 1. Inventory

Record every answer in the migration plan. Each unanswered line is a risk.

- [ ] **Sign-in providers.** Firebase console > Authentication > Sign-in method,
  or Identity Platform > Providers. List each enabled provider: email/password,
  email link, phone, anonymous, Google, Apple, GitHub, Facebook, Microsoft,
  Twitter/X, Yahoo, generic OIDC (`oidc.*`) and SAML (`saml.*`).
- [ ] **Users per provider.** After step 3, count export rows by
  `providerUserInfo[].providerId`, rows with a `passwordHash`, rows without an
  `email`, rows with `emailVerified: false` and disabled rows.
- [ ] **One account per email address?** If the project allows several accounts
  for one email, list the duplicates: TerraScale emails are unique per project.
- [ ] **MFA.** SMS or TOTP second factors and how many users enrolled
  ([multi-factor auth](https://firebase.google.com/docs/auth/web/multi-factor)).
- [ ] **Custom claims.** Every key the code sets with `setCustomUserClaims` and
  every place that reads it ([custom claims](https://firebase.google.com/docs/auth/admin/custom-claims)).
- [ ] **Blocking functions.** `beforeUserCreated` and `beforeUserSignedIn`
  handlers and what they enforce ([blocking functions](https://firebase.google.com/docs/auth/extend-with-blocking-functions)).
- [ ] **Identity Platform tenants.** Multi-tenant projects list each tenant
  ([multi-tenancy](https://cloud.google.com/identity-platform/docs/multi-tenancy-managing-tenants)).
- [ ] **Code.** Run from the app and backend roots:

~~~sh
rg -n "firebase/auth|firebase-admin|@react-native-firebase/auth|firebase_auth|FirebaseAuth|Firebase\.Auth" --glob '!*lock*'
rg -n "onAuthStateChanged|onIdTokenChanged|getIdToken|getIdTokenResult|signInWith|createUserWith|sendSignInLinkToEmail|signInAnonymously|linkWith|multiFactor|RecaptchaVerifier"
rg -n "verifyIdToken|verifySessionCookie|createCustomToken|setCustomUserClaims|revokeRefreshTokens|createSessionCookie|getUserByEmail|listUsers"
rg -n "request\.auth|auth\.uid|auth\.token" --glob '*.rules' --glob '*.json'
rg -n "beforeUserCreated|beforeUserSignedIn|beforeCreate|beforeSignIn|identity\.v2"
~~~

- [ ] **Rules files.** `firestore.rules`, `storage.rules`, `database.rules.json`
  and every condition on `request.auth` or `auth`
  ([rules and auth](https://firebase.google.com/docs/rules/rules-and-auth)).
- [ ] **Sessions.** Where the app keeps Firebase sessions (client SDK
  persistence, session cookies) and how long they live.

## 2. Plan the mapping

Decide each feature with [feature mapping](references/feature-mapping.md). The
short version:

| Firebase | TerraScale |
| --- | --- |
| Email/password | Password sign-in on the hosted pages; hashes cannot be imported. |
| Email link | Email code sign-in (a code, not a link); magic links are not available. |
| Google, Apple, GitHub | The same providers under Social sign-in, with new OAuth clients. |
| Generic OIDC | Generic OpenID Connect social provider. |
| SAML, phone, anonymous | Not available; see the workarounds in the mapping. |
| TOTP or SMS MFA | TOTP and recovery codes; users enroll again. |
| Custom claims | `custom_claims` on the user (copied as `claims` into access tokens), and roles in backend-minted data tokens. |
| Blocking functions | No blocking hook; after-the-fact webhooks only. |
| ID token + `verifyIdToken` | OIDC access token (ES256 JWT, 5 minutes) checked against the project JWKS. |
| `request.auth` in rules | TerraBase Rules on `auth.uid` and `auth.claims`. |

Then fix the identity plan:

- [ ] **User ids.** TerraScale user ids are ULIDs and Firebase uids cannot be
  kept as `sub`. Store the Firebase uid as `external_id` on the imported user
  (at most 255 characters, unique in the project). Assign each Firebase uid one
  app user id (a ULID you generate once) and keep a mapping table
  `firebase_uid -> app_user_id -> terrascale_user_id`. Rewrite data ownership to
  `app_user_id` once; your backend mints data tokens for it
  ([data access for your users](https://docs.terrascale.tech/guides/end-user-authentication#data-access-for-your-users)).
  Every uid keeps its own `app_user_id`, even when accounts are merged.
- [ ] **Who is imported.** Only accounts with an email and `emailVerified: true`.
  Users without an email (phone-only, anonymous) cannot be imported; plan step 4's
  workaround for them. Unverified accounts are skipped and keep using Firebase
  during the bridge; the app asks them to verify there, and a later run imports
  them. Importing them earlier would hand their data to whoever holds the mailbox,
  who may not be the person who created the Firebase account. Never mark an
  unverified address as verified
  ([user profiles](https://github.com/TerraScale/terrascale/blob/main/docs/auth/user-profiles.md#import)).
- [ ] **Duplicates.** Only verified accounts that share an email are merged: one
  is imported and the others point at it with `merged_into`, and you move their
  data in an explicit step. An unverified account that shares an email with
  another is skipped as `unverified_duplicate` and never merged.
- [ ] **Email changes.** Turn off email changes in the Firebase app from the first
  export until the bridge closes: TerraScale's user update cannot change an email.
- [ ] **Bridge window.** A period (for example 14 days) in which the backend
  accepts both Firebase ID tokens and TerraScale access tokens.

## 3. Export Firebase users

Follow [user export](references/user-export.md). In short:

~~~sh
firebase auth:export users.json --format=json --project "$FIREBASE_PROJECT_ID"
~~~

`auth:export` is documented in the
[Firebase CLI auth reference](https://firebase.google.com/docs/cli/auth); it writes
password hashes only for scrypt accounts. The hash parameters (signer key, salt
separator, rounds, memory cost) are under Authentication > Users > the menu above
the user list. TerraScale cannot use them; you need them only to re-import the
hashes into a Firebase or Identity Platform project for rollback. Never print,
paste or commit them: if you copy them, store them in a secret manager as
`FIREBASE_SCRYPT_SIGNER_KEY`, `FIREBASE_SCRYPT_SALT_SEPARATOR`,
`FIREBASE_SCRYPT_ROUNDS` and `FIREBASE_SCRYPT_MEM_COST`.

Read custom claims, MFA enrollments and tenant users with the Admin SDK
`listUsers`, because the CLI reference does not list them in the file format.

## 4. Configure TerraScale sign-in

In the dashboard, open **Authentication** for the project
([end-user authentication](https://docs.terrascale.tech/guides/end-user-authentication#set-up-sign-in-in-the-dashboard)):

- [ ] **Sign-in methods.** Turn on email codes and passwords. Turn **off**
  self sign-up until the import and reconciliation are done, so nobody can create
  an account that collides with an imported one.
- [ ] **OAuth applications.** Register the app: **public** for browser, mobile and
  desktop apps, **confidential** for server-side apps. Use 1–10 exact redirect
  URIs (`https`, or `http` on loopback). Store a confidential secret in the server
  secret store; it is shown once.
- [ ] **Configuration.** Set `redirect_urls` if you want a project allowlist and
  `session_lifetime_seconds` (5 minutes to 30 days, default 14 days). Firebase
  refresh tokens do not expire on a schedule; TerraScale sessions do.
- [ ] **Branding.** Name, logo, theme color and `email_message`. Mail still comes
  from TerraScale's sender; custom sender domains are not supported.

### Social providers

Create a new OAuth client at each provider with your own account
([social sign-in](https://docs.terrascale.tech/guides/end-user-authentication#social-sign-in))
and register the callback the dashboard shows:

~~~text
https://auth.terrascale.tech/ui/<project ID>/social/<provider>/callback
~~~

`<provider>` is `google`, `github`, `apple` or `oidc`. Firebase's
`https://<project>.firebaseapp.com/__/auth/handler` redirect does not apply.

- [ ] **Google:** create a Web application client in the same Google Cloud project
  (APIs & Services > Credentials). Google's `sub` identifies the Google account
  across clients ([Google OpenID Connect](https://developers.google.com/identity/openid-connect/openid-connect)).
- [ ] **Apple:** keep the same Apple developer team; create a Services ID and a
  Sign in with Apple key, then enter the Services ID, Team ID, Key ID and `.p8`
  file. Apple user identifiers are scoped to the developer team
  ([Sign in with Apple](https://developer.apple.com/documentation/sign_in_with_apple/authenticating-users-with-sign-in-with-apple)).
- [ ] **GitHub:** create an OAuth App (Settings > Developer settings).
- [ ] **OIDC:** enter the issuer, client ID and secret of the same IdP Firebase used.
- [ ] Do not change a provider's client ID after cutover: TerraScale matches a
  linked identity by provider, issuer, client ID and subject, so a new client ID
  stops matching existing links.

**How social accounts behave**
([social sign-in](https://docs.terrascale.tech/guides/end-user-authentication#what-end-users-see),
[SocialCallbackEndpoint.cs](https://github.com/TerraScale/terrascale/blob/main/src/TerraScale.Authentication/Features/SignIn/SocialCallbackEndpoint.cs)):

- Accounts are never linked by email alone. A provider identity with a verified
  email that already belongs to an account is refused with "Sign in with your
  password, then connect ... from Security". One whose email the provider does not
  mark verified gets an emailed code, and redeeming it connects the provider.
- The import cannot create provider links: rows accept email, profile, metadata,
  `email_verified`, `external_id` and `password_hash` only.
- So an imported Google-only user signs in **once with an email code**, then
  connects Google on the hosted security page (needs a sign-in from the last
  10 minutes). Tell users this before cutover; the refusal message mentions a
  password they never had.

### Users without a usable email

- **Phone-only users:** before cutover, ask them in the Firebase app to add and
  verify an email (Firebase account linking), then export again. Users who do not
  stay on Firebase until the bridge closes and are then disabled or contacted.
- **Anonymous users:** guest accounts are not available. Keep their state keyed by
  `app_user_id` and ask them to sign up; attach the data after their first
  TerraScale sign-in.

## 5. Import and map

Follow [user export](references/user-export.md#4-import-into-terrascale):

1. Transform `users.json` into import rows: `email`, `email_verified: true`,
   `external_id` (the Firebase uid), `first_name`/`image_url` if you want them,
   and `private_metadata` such as `{"firebase_mfa": true}`. No `password_hash`.
   Each run reads the previous ledger, keeps every `app_user_id`, writes to a new
   folder, emits only uids not imported yet, and lists changed emails.
2. Post batches of up to 500 rows to `POST /v1/{project}/users/import`
   (ten calls per project per minute). Rows commit independently; save every
   response.
3. Record `items[].user.id` in the ledger by `external_id`. Rows in `errors[]`
   stay pending; `user_conflict` means the email or `external_id` already exists
   and is reconciled by email before the next run.
4. Set custom claims with `PATCH /v1/{project}/users/{user}` and
   `custom_claims` (import does not take them).
5. Reconcile: export the TerraScale directory and compare counts and
   `external_id`s with the Firebase export.

## 6. Rewrite app code

Replace the Firebase client with an OpenID Connect library that supports the
authorization code flow with PKCE (`S256`), configured from your project's
discovery document
([sign users in](https://docs.terrascale.tech/guides/end-user-authentication#sign-users-in)).
The runnable [OIDC customer app](https://github.com/TerraScale/terrascale/tree/main/docs-site/examples/oidc-customer-app)
uses `jose` with `node:http`.

| Firebase | TerraScale |
| --- | --- |
| `signInWithEmailAndPassword`, `signInWithPopup`, `sendSignInLinkToEmail` | Redirect to the discovered authorization endpoint; the hosted page offers every enabled method. Add `ui_locales` for language. |
| `onAuthStateChanged`, `currentUser` | Your app session, created at the callback after validating `state`, `nonce` and the ID token. |
| `getIdToken()` sent to your API | The access token (5 minutes); refresh with the refresh token, which rotates on every use. |
| `getIdTokenResult().claims` | Validated access-token claims: `sub`, `email`, `email_verified`, `claims`. |
| `signOut()` | Clear the app session, revoke at `/oauth/revoke`, then `/oauth/logout`. |
| Admin `verifyIdToken(token, true)` | Verify the access token's ES256 signature against `<issuer>/.well-known/jwks.json`, exact `iss`, `aud` = your client ID, and expiry. |
| Admin `setCustomUserClaims` | `PATCH /v1/{project}/users/{user}` with `custom_claims`; it reaches tokens at the next refresh. |
| Admin `updateUser({disabled})`, `deleteUser` | Disable, enable or delete in the dashboard or the SDK project client. |
| Admin `revokeRefreshTokens` | Disable the user, or revoke their sessions (dashboard or SDK). |
| Admin `createCustomToken` | No sign-in equivalent. For data, mint a scoped end-user token on the backend. |

Backend verification in Node.js with `jose`:

~~~js
import { createRemoteJWKSet, jwtVerify } from "jose";

const issuer = `https://auth.terrascale.tech/${process.env.TERRASCALE_PROJECT_ID}`;
const jwks = createRemoteJWKSet(new URL(`${issuer}/.well-known/jwks.json`));

export async function verifyAccessToken(token) {
  const { payload } = await jwtVerify(token, jwks, {
    issuer,
    audience: process.env.TERRASCALE_CLIENT_ID,
    algorithms: ["ES256"],
  });
  return payload;
}
~~~

The Python equivalent with PyJWT is in the
[end-user authentication guide](https://docs.terrascale.tech/guides/end-user-authentication#sign-users-in).
Then look up `payload.sub` in the mapping table to get the user's
`app_user_id` (`merged_into` when set). Before step 4 of the cutover, an unknown
`sub` is an error: refuse it and investigate. From step 4 on, an unknown `sub` is a
new TerraScale sign-up: create a new `app_user_id` and a mapping row with
`firebase_uid: null`. If that person turns out to own a Firebase account (a later
run imports a verified account with the same email), attach it as
[user export](references/user-export.md#4-import-into-terrascale) describes.

Tokens carry `email_verified`. Imported users are verified, and redeeming an
emailed code sets it
([Passcodes.cs](https://github.com/TerraScale/terrascale/blob/main/src/TerraScale.Authentication/Features/Identity/Passcodes.cs));
still check it before features that need a proven mailbox.

Cross-origin `fetch` to `auth.terrascale.tech` is not supported; use browser
navigation and a backend exchange
([capabilities](https://docs.terrascale.tech/guides/authentication-capabilities)).

## 7. Rewrite security rules

Firebase rules that read `request.auth` become TerraBase Rules per database
([TerraBase Rules](https://github.com/TerraScale/terrascale/blob/main/docs/terrabase/rules.md)).
The documented path for browser data access is a short-lived token your backend
mints with `POST /v1/terrascale/projects/{project}/end-user-tokens`: it asserts
`user_id` (use `app_user_id`) and optional `claims.roles`, and TerraBase honors it
for shape and row reads as the rules allow
([data access for your users](https://docs.terrascale.tech/guides/end-user-authentication#data-access-for-your-users),
[scoped end-user tokens](https://github.com/TerraScale/terrascale/blob/main/docs/auth/scoped-end-user-tokens.md)).

| Firebase rule | TerraBase rule |
| --- | --- |
| `request.auth != null` | Any end-user token; requests without one answer `401`. |
| `request.auth.uid == resource.data.owner` | `"read": "row.owner_id = auth.uid"` |
| `request.auth.uid == request.resource.data.owner` | `"set": {"create": {"owner_id": "auth.uid"}}` |
| `request.auth.token.admin == true` | `"'admin' IN auth.claims.roles"` |
| `request.auth.token.email_verified` | Check it in the backend before minting; mint roles accordingly. |

Writes from the browser are not described on the public docs site; send writes
through your backend with a server-only API key
([SQL over HTTP](https://docs.terrascale.tech/guides/sql-http)) and check
ownership there. Moving Firestore data is firestore-to-terrascale's job.

## 8. Verify before cutover

- [ ] Every Firebase uid is in the ledger as `imported`, `merged` or `skipped`
  with a reason; zero unexplained `errors[]`; `changed.json` handled.
- [ ] No `terrascale_user_id` belongs to two `imported` rows, and no unverified
  uid is `merged` or `imported`.
- [ ] `email_verified` is `true` for every imported user in the TerraScale export.
- [ ] A test user of each kind signs in on staging: password user (email code,
  then reset), Google user (email code, then connect), Apple and GitHub users, a
  disabled user (refused). A skipped unverified user still reaches only their own
  data through Firebase.
- [ ] An expired, wrong-audience and other-project token are each refused by the
  backend; a valid one maps to the right `app_user_id`.
- [ ] Data rules: user A cannot read user B's rows; an admin role can.
- [ ] Sign-out revokes the refresh token; refresh after revoke fails.

## 9. Cut over

1. **Freeze Firebase sign-up**: deploy a `beforeUserCreated` blocking function
   that rejects new accounts, or turn off sign-up in Identity Platform.
2. **Delta import**: export again and run the transform with the previous
   ledger into a new folder; handle `changed.json`; import and record.
3. **Ship the bridge build**: the backend accepts both Firebase ID tokens
   (`verifyIdToken(token, true)`) and TerraScale access tokens and maps both to
   `app_user_id`. New sign-ins go to TerraScale. Users already signed in to
   Firebase keep working and see a banner to switch.
4. **Turn on TerraScale self sign-up** if the app allows it, only after the delta
   import is reconciled. From now on, unknown `sub`s get new mapping rows (step 6).
   Run the transform again during the bridge to import users who verified their
   email on Firebase.
5. **Close the bridge**: call `revokeRefreshTokens` for every Firebase user, stop
   accepting Firebase tokens in the backend (ID tokens last one hour;
   `checkRevoked` ends them at once,
   [manage sessions](https://firebase.google.com/docs/auth/admin/manage-sessions)),
   and remove the Firebase client.

**If a cutover step fails halfway:** each step can be re-run. Import rows commit
one by one, so re-submit only rows that failed and resolve `user_conflict` by
email; a lost import response can leave committed rows, so reconcile with the
export before retrying. Until step 5, Firebase remains a working sign-in for
anyone not yet moved.

## 10. Roll back

Keep the Firebase project, users, provider settings and hash parameters unchanged
until a set date after the bridge closes.

- Before step 5: switch the frontend flag back to Firebase sign-in; the backend
  still accepts Firebase tokens. Remove the Firebase sign-up freeze.
- After step 5: re-enable Firebase sign-in and stop revoking; users sign in again.
- Users who signed up on TerraScale during the bridge have mapping rows with
  `firebase_uid: null`. Create each in Firebase with the Admin SDK `createUser`
  (or `importUsers`), choosing the uid, with `emailVerified: true` (they proved
  the mailbox at sign-up). Write that uid into their mapping row so the bridge
  maps it to their existing `app_user_id`, then ask them to reset their password,
  because the TerraScale export holds no credentials
  ([manage users](https://firebase.google.com/docs/auth/admin/manage-users)).
- Data stays valid in either direction because it is keyed by `app_user_id`.
- Disable, rather than delete, TerraScale users created by the import if you
  abandon the migration, until you are sure.

## Account takeover and other risks

- **Unverified emails**: unverified Firebase accounts are not imported and never
  merged. An attacker who registered someone else's address in Firebase keeps
  only their own `app_user_id` on Firebase and gets no TerraScale account. The real
  owner who later signs up on TerraScale gets a fresh `app_user_id`, not the
  attacker's data.
- **Email changes**: blocked in the Firebase app from the first export until the
  bridge closes; any change found in `changed.json` is fixed by deleting and
  re-importing before cutover, so no TerraScale account stays on an old mailbox.
- **Collisions**: self sign-up stays off until import and reconciliation finish.
- **Social linking**: never by email, so a provider account cannot take over an
  imported account; users connect providers after a fresh sign-in.
- **MFA gap**: second factors do not move, and the public docs describe no way
  to require MFA per project or to read a user's enrollment from your app. Flag
  former MFA users in `private_metadata`, ask them to enroll TOTP on the hosted
  security page right after their first sign-in, and keep your own extra checks
  on sensitive actions for flagged users during the bridge.
- **Disabled users**: import them, then disable them before cutover.
- **Orphans**: every Firebase uid ends in the ledger as imported, merged or
  skipped with a reason, and every TerraScale sign-up gets a mapping row.
