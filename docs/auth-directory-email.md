# Directory exports and email branding

Organization owners and admins open Authentication → Users → Export end-user directory. Each download requests at most 100 users through the official SDK using the existing session bearer. Search filters do not affect the export. Download the next page explicitly, or save its opaque resume cursor and paste it into this project's export form later. Start over clears the cursor. A changed project requires reloading Users.

JSON includes `items`, `next` and `created_before`. CSV has `id,email,email_verified,created_at,disabled,linked_providers`; linked providers are JSON within a quoted cell. CSV cells that could become spreadsheet formulas, and cells beginning with an apostrophe, receive an apostrophe prefix; remove one leading apostrophe when importing. Both downloads expose `X-Export-Next` and `X-Export-Created-Before`. An absent X-Export-Next header and JSON next=null end the scan. Downloads are private and use `Cache-Control: no-store`; the browser holds one page at a time without automatically accumulating the directory.

This is a creation-cutoff scan, not a transaction snapshot. Later creations are excluded; updates and deletions may be visible between pages. ts-auth authorizes and audits every page. A cursor does not grant access after permissions or credentials are revoked.

Authentication → Branding reuses app name, HTTPS logo and accent color for verification, sign-in and password reset emails. The optional email message accepts up to 500 plain text characters, with newlines; clearing it removes the message. CRLF line endings normalize to LF, other control characters are rejected, and rendered content is escaped. The verified TerraScale sender remains unchanged.

Consumes the reproducibly qualified SDK 0.0.12 export DTOs/method and `Branding.EmailMessage`, backed by the merged ts-auth REST contract. No dashboard storage format or credential changes.
