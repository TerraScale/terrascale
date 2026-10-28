# Token requests: one client read, Server-Timing, conflicts

OpenIddict looks up the requesting client six to eight times while it serves one token or authorization request.
Entity caching stays off globally (`OidcServer.AddOidc`): a cross-request cache would also hold authorizations and
token entries, and revocation winners must come from the regional store on every request. `OidcApplicationStore`
instead keeps the client it read in `HttpContext.Items` for the rest of that request, so storage serves it once. A
create, update or delete of that client in the same request drops the copy.

Token requests now carry `Server-Timing: app;dur=…, storage;dur=…;desc="N calls, W writes"`, like bearer API calls.
Every write waits for another region's acknowledgement, so `W` and the storage time show where a slow grant spent
its time. Counts reveal nothing the client does not already hold.

An `OpenIddictExceptions.ConcurrencyException` (a store's conditional write lost inside an OpenIddict flow) answers
`409 conflict`, like `TerraBasePreconditionFailedException` since #825, instead of `500 internal_error`.
