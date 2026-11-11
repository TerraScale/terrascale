# Node HTTP transport

The SDK captures native `globalThis.fetch` when a client is constructed, unless
the caller supplies `fetch`. Native fetch owns connection pooling. The SDK does
not install a global dispatcher or supply a private dispatcher, so application
proxy, TLS and interceptor policy applies to both raw fetch and SDK requests.

HTTP/2 negotiation follows that transport's policy; the SDK does not promise
HTTP/2 on every supported Node version's default dispatcher. Applications that
already configure an HTTP/2 dispatcher retain it. Browser fetch remains native.

A prototype SDK-owned dispatcher was rejected: it bypassed application policy
and an unrestricted pool opened multiple HTTP/2 connections during a burst.
Latency comparisons must use the same transport policy for raw HTTP and the SDK.
