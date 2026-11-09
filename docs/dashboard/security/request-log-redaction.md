# Request logging without credentials

Dashboard timing records identify the matched route template (or `[unmatched]`),
never the request path. Invitation tokens, query values, cookies, authorization
headers and full outbound URLs must not be written to application logs.

The logging boundary sanitizes ASP.NET, System/HttpClient and MCP library records
before configured providers receive them, including structured state, scopes and
exceptions. It retains categories, event IDs, levels, numeric status/duration
fields and standard HTTP methods. Unknown methods become `[other]`. Only valid
server request IDs and hexadecimal trace/span IDs survive scopes. Application
records retain their explicit operational messages; exception sinks record the
exception type and, for Stripe failures, HTTP status instead of provider text.
An unexpected request failure (HTTP 500) is logged once as `Dashboard request failed on
<route template>: <type> at <frames>`, with up to 12 frames as `Type.Method:line` for the
exception and up to two inner exceptions. Frames name code, never values, so a failure is
traceable from its request ID without logging the exception message.
An optional page part that fails unexpectedly (`OptionalPanel`, see
[analytics](../analytics.md#platform-data-and-optional-panels)) does not fail the request; it is
logged at Error as EventId 44, `Dashboard panel <panel> failed on <route template>; the page
rendered without it: <type> at <frames>`, with the same frames and no message.
New logging calls must follow the same rule: never log arbitrary exception objects,
messages, request values or user input. Route templates must remain static metadata.

The integration journey `SensitiveLoggingJourneyTests` captures rendered messages,
structured values, exceptions and scopes at Trace level during real local HTTP,
HttpClient, billing and authenticated MCP requests. Synthetic secrets must be
absent while hosting, dependency, timing and warning events remain visible. It
uses isolated test providers and does not create production users.

This change affects server diagnostics only; running-app screenshots and videos
are not applicable because no visual flow changes. CSRF cookie `__Host-` prefix
hardening remains a separate change; existing CSRF behavior is unchanged.
