# HTTP/2 and connection reuse

SDK-owned synchronous and asynchronous HTTPX clients enable HTTP/2 negotiation
over HTTPS, with HTTP/1.1 fallback. The HTTP/2 dependencies are installed with
the SDK. This also applies to standalone API-key token sources.

Keep a client for the application's lifetime and close it with its context
manager or `close()`. Database and collection handles share that client's pool;
API-key exchange uses the same HTTPX client. Each origin has its own pooled
connections. Reconstructing a client per request prevents this reuse.

An explicitly supplied `http_client` retains the caller's transport policy and
ownership. TLS validation, redirect refusal, token refresh and retry rules remain
in effect. The in-memory protocol tests exercise HTTPX/HTTPcore framing, fake
client ALPN offers, fake selection, response parsing and connection reuse; they do not perform real
TLS or establish a production latency improvement.
