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
