# MCP tool metadata and host isolation

Dashboard MCP schemas are built once per process from the same explicit fifteen
attributed tool types. A private `Lazy<McpServerTool[]>` uses
`ExecutionAndPublication` so concurrently starting hosts share one schema build.
The original `MethodInfo` goes to MCP 2.2's `McpServerTool.Create`; the SDK still
owns descriptions, defaults, annotations, input/output schemas and structured
content handling. The cached tool metadata is never modified.

Each invocation constructs its tool from the current request's service provider
with `ActivatorUtilities.CreateInstance`. The cache captures only the tool type,
never a host, service provider, tool instance, HTTP context or principal. Current
tool parameters are JSON values; constructor dependencies resolve at invocation,
without a creation-time service provider. A future tool accepting injected method
parameters must preserve that distinction when changing registration.

Authentication, account and project authorization, error filters, and request
cancellation still run per request. This is a metadata cache, not an authorization
cache. The HTTP host-isolation journey starts independently configured hosts
concurrently, verifies different users and measurement dependencies, rejects
foreign scopes and tokens, and exercises a surviving host after its peer shuts
down. Discovery metadata remains identical across those hosts and after disposal.

This avoids repeated MCP/AI JSON schema reflection during multi-host startup.
It does not address ASP.NET `RequestDelegateFactory` expression compilation;
startup and HTTP deadlines and test parallelism remain unchanged.
