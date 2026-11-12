# Hosted sign-in page support

Hosted refusal pages, browser not-found pages and error alerts show `Request ID: …`.
This is the same correlation ID returned in `X-Request-ID`; users can give it to
support without sharing credentials or an authentication code. Successful pages
keep their existing presentation.

Footer links to another origin open in a new tab with `noopener noreferrer`, so
visitors keep their sign-in step open. Links on the hosted service's public origin
keep their existing navigation. Default Privacy and Terms links go directly to
`https://docs.terrascale.tech/legal/privacy` and `/legal/terms`.

Project branding and the visitor's light, dark or system preference still apply.
These presentation changes do not change authentication, storage or HTTP APIs.

Hosted links retain supported `ui_locales` tags (`en` and `pt-BR`) in preference
order and canonical casing, ignoring unsupported tags. Lists longer than 256
characters are ignored before splitting; supported tags are dropped from the
end to fit 64 characters. The first retained tag selects the page's language;
otherwise the page uses Accept-Language. Form actions carry the retained locale
list; the hidden `next` field carries the continuation for POSTs.

Continuations must fit the 4096-character limit after URL escaping non-ASCII
characters. A signed-in page without a usable continuation offers the platform
dashboard, from `AuthOptions.DashboardOrigin`, or the same tenant's security page
for a project account. Neither destination comes from a request-supplied client
redirect. A signed-out page without a continuation offers sign-in again.
Returning to sign-in or sign-up with a live session and no continuation shows
the form, allowing a different account to replace the session.

Passkey sign-in follows the automatic continuation link, then the signed-in
card's visible default destination link, allowing same-origin or HTTPS targets.
If neither is present, it navigates to the page's same-origin sign-in GET
address, so a page returned by a POST never needs to resubmit that POST.
