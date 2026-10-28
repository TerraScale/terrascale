# Node benchmark transport controls

These helpers count raw/SDK attempts and wire sends, validate exact region headers,
and record socket ALPN and new API connections. They are benchmark-only code.
`control.mjs` uses a trusted synthetic TLS CA and real SDK requests, without a
production tenant. It rejects HTTP/2 fallback, duplicate response region headers,
and hidden retries, checks connection reuse, and cancels a stalled token exchange.

Use clean ts-js `b1bfe76254d8cc3e8e0aaa1a87dd599e7af4efe0`, Node 22/24 and its
committed lock: `npm ci --ignore-scripts` (Undici 7.30.1). Stage this directory
under that SDK checkout's `benchmark/bench/node/` so package self-reference uses
the pinned source. Run `node control.mjs` through `ts-heavy-any nice -n 19`.
No registry release, production credential, tenant or extra dependency is needed.

Six TLS cases passed on delta, Node 24.21.0, npm 12.2.0. Default pools opened
17 H1 or 16 H2 API connections during the c=16 burst; later sequential reads
opened none. No speedup, live SDK parity or production cleanup is established.
