# Storage timeouts answer 503

ts-dotnet throws `TimeoutException` when TerraBase does not answer within its request timeout (30 s). ts-auth caught only
transport failures (`HttpRequestException`) as storage unavailability, so a timeout fell through to the generic
`500 internal_error` envelope. `ErrorEnvelopeMiddleware` now answers a timeout with `503 temporarily_unavailable`, like
every other storage failure. Every `temporarily_unavailable` envelope carries `Retry-After: 2`; before, only the token
endpoint's deadline set it. Token requests keep their own 10 s deadline (`TokenEndpointDeadline`).
