# Starter ownership migration checklist

The current template remains **unqualified until migration is finalized**.
This checklist prepares against the TS-589 target API contract; it does not
claim that contract is released. TS-589 must ship first, including the new
routes, ts-auth JWT data authorization, and ts-go/ts-js JWT support. Await the
**RELEASED digest** before finalizing the migration or claiming qualification.

1. **Select released SDKs and respect service ownership.** ts-auth owns identity,
   organizations, projects, memberships, roles, invitations, and API keys;
   TerraBase owns project-scoped data. Use the public SDKs for all service
   communication, including authorization and credential exchange. The reviewed
   published `@terrascale/sdk` 0.0.2 rejects JWTs and requires legacy `tb_` keys;
   the template's pinned 0.3.0 is unavailable. Neither qualifies the target.
   Replace direct dashboard `/api/auth/oauth2/*` and
   `/api/auth/web-starter/delegation` calls with released SDK capabilities.
   Do not bypass SDK validation or introduce raw HTTP fallbacks.

2. **Use project-scoped paths and database names.** `{project}` is the ts-auth
   project ID. `{database}` is an immutable database **name**, unique within its
   project, matching `[a-z][a-z0-9_-]{0,62}`; collection names follow the same
   rule. Update configuration, CLI handoff, and SDK calls that currently pass a
   database ID. The relevant public data paths are:

   ```text
   /v1/projects/{project}/databases/{database}/collections/{collection}
   /v1/projects/{project}/databases/{database}/collections/{collection}/documents
   /v1/projects/{project}/databases/{database}/collections/{collection}/documents/{id}
   POST /v1/projects/{project}/databases/{database}/collections/{collection}/query
   POST /v1/projects/{project}/databases/{database}/sql
   POST /v1/projects/{project}/databases/{database}/transactions
   GET  /v1/projects/{project}/databases/{database}/collections/{collection}/shape
   ```

   Collection/document reads and writes retain their documented methods.
   Create databases only through `POST /v1/projects/{project}/databases`;
   branch through `POST /v1/projects/{project}/databases/{database}/branches`.
   Read schema at `GET /v1/projects/{project}/databases/{database}/schema`
   or `/schema/{collection}`. Old organization/management paths have no aliases;
   data-plane `PUT` database creation is removed.

3. **Authorize with ts-auth project access JWTs.** Data calls use
   `Authorization: Bearer <ts-auth access token>` with the trusted ts-auth issuer,
   TerraBase audience, and `project_id` matching `{project}`. A `database` claim
   narrows access to that database name. There is **no `tb_` exception**. API keys
   are ts-auth client credentials exchanged by the SDK for short-lived JWTs
   (`grant_type=client_credentials`, naming the project); signed-in users need
   RFC 8693 token exchange naming the project.

   Use `documents:read` for collections/documents/queries, `documents:write`
   for collection changes/document writes/transactions, and `shapes:read` for
   live shapes. `databases:read` covers database/schema reads and read-only SQL;
   `databases:write` covers database creation/branching/deletion. Replace the
   template's singular `database:read`/`database:write` assumptions.

   **Pending if OAuth is retained:** a registered ts-auth browser public client,
   exact callback, authorization-code PKCE (S256), and SDK-supported project
   token exchange and renewal. Keep credentials in tab memory and clear them
   on disconnect; never expose privileged keys through `NEXT_PUBLIC_*`.
   A server-only key exchanged through the SDK is an **undecided alternative**,
   not an approved replacement for browser sign-in.

4. **Migrate live updates to the released shape contract.** The reviewed SDK
   bootstraps `offset=-1` on the application server because browsers hide the
   initial `307` metadata. Use the SDK to obtain the handle and snapshot offset;
   pass that position to the browser for the identical collection/filter.
   Do not forward credentials to a redirect target. Apply changes in received
   order, advance with `next_offset`, and preserve handle/offset checkpoints.
   Use `live=true` only with decimal log offsets and echo `TB-Shape-Cursor` as
   `cursor` on the next live request. Snapshot offsets are
   `snap.<head>.<base64url-id>`; handles are required after bootstrap.
   On `must_refetch`, discard local state and bootstrap a fresh snapshot.
   Verify this behavior against the released JWT-capable SDK before replacing
   the template's SQL subscription API.

5. **Accept only released, connected evidence.** Record the RELEASED source and
   artifact digests for the API/auth services and JWT-capable ts-go/ts-js SDKs;
   pin an installable starter SDK and verify generated-app install/build.
   Confirm SDK-only calls, database-name routing, successful required scopes,
   `404` for a mismatched project or a database outside the token's restriction,
   and `403 forbidden_scope` for missing scopes. With real cloud accounts,
   qualify two independent sign-ins, writes
   visible in both tabs, credential renewal, disconnect/catch-up, revocation
   within the declared bound while the peer remains usable, and fresh sign-in
   resuming reads/writes. Also verify shape bootstrap/resume/refetch and the
   fresh-machine five-minute setup target. Keep evidence credential-free;
   local fixtures do not qualify the cloud journey. Until the auth choice,
   released artifacts, migration, and these gates are complete, retain the
   template's unqualified status.
