# TerraScale.Logging

Logging for TerraScale services, built on [ZLogger](https://github.com/Cysharp/ZLogger). Every log entry is one line of compact
JSON on stdout (TerraScale record v1), the same shape as the ts-orchestrator Go logs.

```csharp
builder.Logging.AddTerraScaleLogging("terrabase", ("TerraBase.Features.ApiKeys.ApiKeyAuthenticationHandler", LogLevel.Warning));
```

The optional tuples set a minimum level for a category the service owns, for example a framework
base class that logs under the service's own type. Each category may appear once.

Services can log with ZLogger. Holes take `{x:@Name}`. A format on a hole changes how `msg` spells the template (`{Name,0.00}`), so services format the value first if they need one. Never use `:json`, because it breaks NativeAOT:

```csharp
logger.ZLogInformation($"User {id:@UserId} signed in");
```

Each record is written on the logging thread, so `trace_id` and `span_id` are the caller's.

Each record has these keys, in this order when present:

| Key | Source |
|-----|--------|
| `time` | UTC, RFC 3339 with microseconds |
| `level` | `DEBUG`, `INFO`, `WARN`, `ERROR` |
| `msg` | Message template (constant text), else the formatted message |
| `service`, `region`, `node` | `TS_SERVICE` (falls back to the name passed in), `TS_REGION`, `TS_NODE` |
| `trace_id`, `span_id` | `Activity.Current` |
| `component` | Logger category |
| `event_id` | Event id, when non-zero |
| state and scope properties | Structured values, typed as JSON |
| `error`, `stack` | Exception type and message, and `ToString()` |

A plain string scope is written as `Scope`.

A state or scope property that reuses a fixed key is written as `attr.<key>`.
When running under systemd with output to the journal (`JOURNAL_STREAM` is set),
each line starts with its syslog priority: `<3>` error, `<4>` warning, `<5>` info, `<7>` debug. journald stores it as `PRIORITY`.

Default filters are set in code: Information overall, `Microsoft.AspNetCore` and
`System.Net.Http.HttpClient` at Warning, `Microsoft.Hosting.Lifetime` at Information, then the service's own categories.
There are no options to turn on or off. Configuration `Logging` sections cannot change the stdout levels, but rules other providers register stay in effect.
