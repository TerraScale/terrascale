# Project user export and email message

`auth.Projects.ExportUsersAsync(accessToken, projectId, limit, after)` calls
`GET /v1/{project}/users/export` with the caller's platform sign-in token and
returns one JSON `TerraScaleAuthUserExportPage(Items, Next, CreatedBefore)`.
The project export DTO is distinct from retired legacy SCIM directory models.
Items are `TerraScaleAuthDirectoryUser(Id, Email, EmailVerified, CreatedAt,
Disabled, LinkedProviders)` with `TerraScaleAuthLinkedProvider(Provider,
ClientId, Subject)`: the producer's allowlist, with no credentials, MFA,
sessions or tokens.

The producer requires the project admin role and commits a
`project.users_exported` audit event on every page; an audit failure answers
503 `temporarily_unavailable` with no data. Low roles answer 403
`forbidden_role` and invisible projects 404 `not_found`. Because each call is
audited, the SDK sends it once and never retries. `limit` is 1–100 (omitted
means the producer's 100); `after` is the previous `Next`, non-empty, at most
8192 characters and percent-escaped. Invalid input throws before any request.

The scan uses the first page's `CreatedBefore` creation cutoff and is not a
transaction snapshot: later accounts are excluded, deletions disappear and
fields reflect each page's read. Save a page before persisting its cursor and
deduplicate by `Id`. The SDK exposes only the JSON format; CSV and the
`X-Export-*` headers are not wrapped.

`TerraScaleAuthProjectBranding.EmailMessage` is an optional `init` property
(`email_message`, at most 500 plain-text characters) shown in verification,
sign-in and password-reset mail. It is omitted when null; as with other
branding fields, sending branding without it clears it. Existing constructor
calls and positional deconstruction are unchanged.

Evidence: `fixtures/project-tenant.json` (`export_users`) and
`TerraScaleAuthProjectsContractTests`, HTTP fakes only. Producer revision and
source hashes are in `user-export-provenance.json`. Live-service and installed
package qualification follow the SDK merge.
