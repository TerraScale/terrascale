# Node HTTP transport

The SDK captures native `globalThis.fetch` when a client is constructed, unless
the caller supplies `fetch`. Native fetch owns connection pooling. The SDK does
not install a global dispatcher or supply a private dispatcher, so application
proxy, TLS and interceptor policy applies to both raw fetch and SDK requests.

HTTP/2 negotiation follows that transport's policy; the SDK does not promise
HTTP/2 on every supported Node version's default dispatcher. Applications that
already configure an HTTP/2 dispatcher retain it. Browser fetch remains native.

`test/node-http2.test.js` uses isolated synthetic TLS fixtures, not TerraBase or
ts-auth qualification. Each child process owns its CA trust and an application
dispatcher. A native fetch and two SDK clients share that dispatcher's single
connection: 16 concurrent HTTP/2 reads, or sequential HTTP/1.1 fallback reads.
The fixture checks protocol, authorization and paths, and closes the dispatcher.
Undici is a development dependency for this check, not a published runtime
dependency. These are transport assertions, not measured latency improvements.

A prototype SDK-owned dispatcher was rejected: it bypassed application policy
and an unrestricted pool opened multiple HTTP/2 connections during a burst.
Latency comparisons must use the same transport policy for raw HTTP and the SDK.
