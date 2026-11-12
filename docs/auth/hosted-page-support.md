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

Hosted links retain `ui_locales` only for a list of supported email locale tags
(`en` and `pt-BR`), with a total length of at most 64 characters. Invalid lists
use the same language negotiation as an absent parameter. Form actions carry
the locale list; the hidden `next` field carries the continuation for POSTs.

Continuations must fit the 4096-character limit after URL escaping non-ASCII
characters. A signed-in page without a usable continuation offers the platform
dashboard, from `AuthOptions.DashboardOrigin`, or the same tenant's security page
for a project account. Neither destination comes from a request-supplied client
redirect. A signed-out page without a continuation offers sign-in again.
Returning to sign-in or sign-up with a live session and no continuation shows
the signed-in page and its destination link.

Passkey sign-in follows same-origin or HTTPS continuation links. Without one,
it navigates to the page's same-origin sign-in GET address, so a page returned
by a POST never needs to resubmit that POST.
