# Project directory portability and authentication mail

`GET /v1/{project}/users/export` requires the caller's TerraScale sign-in token
and the project's admin role on **every** request. An unrelated project answers
404; a viewer answers 403. Responses use `Cache-Control: no-store` and an
attachment filename. Each returned page first commits a `project.users_exported`
organization audit event with the actor, project, format and row count. An audit
failure returns no export data.

Use `limit=1..100` (default 100), `format=json|csv` (default JSON), and the prior
page's opaque `next` as `after`. JSON returns `items`, `next` and `created_before`.
Both formats answer `X-Export-Created-Before`; `X-Export-Next` is absent at EOF.
Persist the cursor only after saving its page; retrying a page is safe but may
return updated profile fields. Store each page once and deduplicate by `id`.

This is a creation-cutoff scan, **not a transaction snapshot**. Accounts created
at or after the first page's cutoff are excluded. Immutable storage document IDs
determine order, not user ULIDs. Deletions disappear and fields reflect each
page's read. Replication lag and creator clock skew can omit accounts. For a
reconciled directory, pause identity changes and allow replication to catch up
before starting; compare against the admin user list under those same conditions.
Cursors are query-bound to the tenant and cutoff; they grant no access.

The only fields are `id` (stable user ULID), `email` (normalized address),
`email_verified` (possession proven at account creation), `created_at`, `disabled`
and `linked_providers` (`provider`, public `client_id`, `subject`). There is no
arbitrary application metadata field in the account model. No password or hash,
MFA/recovery material, session, token, key or internal authentication state is
exported. Importing this directory elsewhere preserves neither credentials nor
authenticated sessions. Disabled accounts are included.

CSV uses the same columns; `linked_providers` is a JSON array inside one quoted
cell. Cells are RFC 4180 quoted. Cells beginning with apostrophe or `=+-@`, tab,
CR or LF receive one apostrophe prefix to prevent spreadsheet execution; remove
one leading apostrophe when importing those cells. JSON preserves exact values.

Project admins set `branding.name`, `logo_url`, `theme_color` and `email_message`
through `PUT /v1/{project}/settings`. These apply to verification, sign-in and
password-reset mail. The optional message is at most 500 plain-text characters,
with newlines permitted; HTML and template expressions are not executed. Fixed
authentication instructions, tenant-bound codes, the verified TerraScale sender,
anti-phishing footer, shared mail budget and delivery accounting remain in place.
Missing branding uses the project name and platform appearance. A settings-read
failure uses the safe platform default. No custom sender domains or credentials
are accepted. This adds optional settings fields; no data migration is required.
