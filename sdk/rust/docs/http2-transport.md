# HTTP transport and connection reuse

The SDK enables reqwest's HTTP/2 support for both `ReqwestTransport` and the
project client. HTTPS negotiates HTTP/2 through TLS ALPN when the server offers
it and uses HTTP/1.1 otherwise. Cleartext requests retain reqwest's HTTP/1.1
behavior; the SDK does not require HTTP/2 prior knowledge.

Keep a client for the lifetime of the application or worker. Handles and cloned
transports share the client's connection pool; constructing a new client per
request creates another pool. Buffered requests consume response bodies before
returning so that their connections can be reused. Changes and shape streams
keep their responses active; consume or drop those streams when finished to
release their resources.

TLS certificate and hostname validation, authorization, redirect refusal and
retry rules remain in effect. The isolated TLS tests check both client surfaces,
HTTP/2 negotiation with sixteen concurrent reads on an established connection,
and sequential HTTP/1.1 reuse. These are protocol and pooling checks, not a
latency benchmark or proof of production performance.
