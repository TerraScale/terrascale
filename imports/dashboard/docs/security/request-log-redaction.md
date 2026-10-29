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
