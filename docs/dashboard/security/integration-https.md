# Isolated integration HTTPS

Dashboard integration hosts use loopback TLS with a fresh certificate/private key
per host. An immutable test-process authority signs them; nothing is written to a
key file or installed in a machine trust store. HttpClient accepts normal public
PKI or the test authority with hostname and server-auth checks. Browser contexts
accept the isolated test certificate while Chromium still enforces Secure and
`__Host-` cookie rules; a real browser regression includes a valid Domain-cookie
control and rejects the host-prefixed Domain variant.

This is test transport only. Production cookie rules and TLS configuration are
unchanged by this prerequisite. No visual flow changes; screenshots are not needed.

The Chromium journey belongs to the existing Slow browser suite. Fast retains
HTTP integration coverage and does not require a browser installation.
