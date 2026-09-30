# TerraScale Go reference application

This customer app uses the public Go SDK for ordinary ts-auth OIDC sign-in,
server-held scoped database credentials, and notes bound to the verified issuer
and subject. It supports create, read, version-checked update, checking the
original uncertain transaction, and logout with token revocation. It has no
imports from dashboard internals and never constructs database HTTP requests.

This is source preparation, not a deployed or released example. Use the SDK
package selected by the integration candidate's immutable source/artifact
manifest. The existing main-branch SDK version does not contain these native
APIs. A published immutable package version and an independently installed,
public HTTPS journey are still required before this is advertised as ready.

## Configuration

Provide these environment variables from your own scoped qualification allocation:

| Variable | Value |
| --- | --- |
| `REFERENCE_ORIGIN` | App's exact public HTTPS origin |
| `TERRASCALE_AUTH_ISSUER` | Exact registered issuer, including the instance slug |
| `TERRASCALE_CLIENT_ID` / `TERRASCALE_CLIENT_SECRET` | Registered confidential OAuth client using `client_secret_basic` |
| `TERRASCALE_DATA_KEY` | Server-held native key restricted to this app's tenant/database/shard and required collection operations |
| `TERRASCALE_TENANT_ID` / `TERRASCALE_DATABASE_ID` / `TERRASCALE_COLLECTION_ID` | Canonical lowercase UUIDs |
| `TERRASCALE_SHARD` | Exact unsigned decimal shard ID |
| `TERRASCALE_SCHEMA_GENERATION` / `TERRASCALE_SCHEMA_DIGEST` | Provisioned positive generation and lowercase 64-character hex digest |
| `TERRASCALE_OWNER_FIELD_ID` / `TERRASCALE_BODY_FIELD_ID` | Distinct provisioned positive numeric UTF-8 field IDs |
| `REFERENCE_LISTEN` | Optional local listener, default `127.0.0.1:8080` |

Register callbacks exactly as `${REFERENCE_ORIGIN}/callback` and
`${REFERENCE_ORIGIN}/signed-out`. Terminate managed HTTPS at the public app
origin and forward to its private listener. Set secrets in server environment
or a secret manager; never put them in browser storage, copied examples or URLs.

Run from the selected SDK source package:

```sh
go run ./examples/reference-app
```

Hosted data routing leaves `DataBaseURL` empty: the SDK uses authenticated Atlas
selection of the closest eligible direct regional endpoint. It never falls
back to the test API host family. The admitted SDK currently closes conditional
writes through discovery and single-writer commits at every origin. The app
therefore reports unavailable write capabilities instead of weakening
conditions, switching origins or pretending a connection is ready.

For isolated development only, set `REFERENCE_DEVELOPMENT=true`; loopback HTTP
and an explicit `REFERENCE_DEVELOPMENT_DATA_URL` become available. This can
exercise supported leaderless absent/version producer semantics. It does not
establish global CAS, security-state writer admission or hosted readiness.
Never use development mode as the production route around missing capabilities.

## User flow and validation

1. Open the app, select **Sign in**, and complete the ordinary OIDC code/PKCE flow.
2. Create a note. The server writes the verified issuer/subject owner; supplied
   browser owner fields cannot override it. Follow the returned note link.
3. Read and update that note. Foreign, absent, deleted and conflicted records
   produce the same scoped not-found result. Updates retain the exact native
   state version and causal context.
4. If a dispatched write has an uncertain result, check **Check pending write**.
   Unknown, pending and expired outcomes never clear the original operation or
   permit a replacement write. The app does not automatically retry mutations.
5. Select **Sign out and revoke session tokens**. Local data access closes before
   provider revocation. A failed revoke stays explicitly incomplete and can be
   retried. Successful revocation shows a same-origin completion page. Follow
   **Finish sign-out with identity provider** to the SDK-validated provider URL;
   browser sign-out is complete only after the state-bound callback. The app
   retains its restrictive form CSP instead of redirecting a form to another origin.

The app verifies active sessions with the public SDK introspection operation
before database access. Cookies are Secure/HttpOnly with SameSite=Lax in hosted
mode. Authorization attempts are bound to the browser and consumed once before
code exchange. Mutations require exact-origin and synchronizer-token checks.
User text is escaped, and failure responses contain application-owned messages and a
random support ID rather than provider errors or credentials.

Sessions, authorization attempts and pending operations are bounded, expiring,
process-local reference state. Run one replica for local source checks. Restart
or session expiry can lose unresolved write records; this app is not ready for
restart/recovery or multi-replica qualification. A durable admitted session and
operation store is required before those acceptance cases. Never recreate an
unresolved effect after restarting it.

Query and subscriptions return an explicit unsupported result until their
required contracts are admitted. Schema installation, key issuance/rotation,
Connect flow's authenticated first-request proof, public HTTPS, independent
package installation, tenant isolation and deployed revocation remain open
qualification requirements. Local handler fixtures cannot close those gates.
