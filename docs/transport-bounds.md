# Fixed transport limits

Document, management and platform clients use a 30-second request deadline, including endpoint
resolution, token acquisition and reading the response. Earlier caller cancellation still wins.
An SDK deadline throws `TimeoutException`; caller cancellation keeps the caller's token.
Responses are bounded at 64 MiB, preserving the producer's largest escaped document pages.
Shape and Prometheus responses have their existing fixed 8 MiB bounds.

`RequestTimeout` and `MaxResponseBytes` were removed from the three public client option records.
Remove these initializers when upgrading. Credentials, endpoint resolvers, region pins and caller
cancellation retain their existing contracts. The SDK never disposes the caller's `HttpClient`.

Change feeds and NDJSON export apply the 30-second deadline to acquisition, then retain their streaming
lifetime: cancel or dispose the enumerator to close it. Stalled reads stop after 45 seconds. NDJSON
records are individually bounded at 64 MiB. An early stop establishes no complete-export proof.
Requests are sent once, including mutations whose outcome may be unknown after a timeout or disconnect.

These resource limits add no producer API or retry behavior. HTTP contract fixtures verify bounded
reads, deadlines, cancellation, independent token acquisition and client ownership; production smoke
remains a separate qualification gate. Install from source/local packs while NuGet publication is held.
