# Fixed transport limits

Database, management and platform clients use a 30-second request deadline, including endpoint
resolution, token acquisition and reading the response. Earlier caller cancellation still wins.
An SDK deadline throws `TimeoutException`; caller cancellation keeps the caller's token.
Responses are bounded at 64 MiB. Prometheus responses have their fixed 8 MiB bound.

The public client option records expose no request timeout or response size. Credentials, endpoint
resolvers, region pins and caller cancellation retain their existing contracts. The SDK never disposes
the caller's `HttpClient`. Requests are sent once, including mutations whose outcome may be unknown
after a timeout or disconnect.

These resource limits add no producer API or retry behavior. HTTP contract fixtures verify bounded
reads, deadlines, cancellation, independent token acquisition and client ownership; production smoke
remains a separate qualification gate. Install from source/local packs while NuGet publication is held.
