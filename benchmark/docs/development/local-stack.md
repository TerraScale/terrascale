# Local three-member stack (E-15, historical)

> **Historical design record.** The stack script, Rust member binary, and Make
> targets described below are retired and are not part of the current C#
> solution. Use the repository [README](../../README.md) and
> `TerraBase.slnx` for current restore, format, build, and test commands. The
> remaining text is retained only to explain the earlier qualification design.

The workspace [GOAL.md](../../../GOAL.md) (G1) owns the requirement; this
page owns the mechanics. `make stack` boots the harness that our CI and the
P-3 journey (`ts-typescript/test/journey.test.ts`) run against. The binary
never leaves our infrastructure (D-50): everything here is loopback-only
development scaffolding, never shipped.

Members run as local processes with distinct ports and data dirs: the
script builds `target/debug/terrabase-server` once via
`cargo build --locked -p terrabase-server` (the same artifact
`cargo run -p terrabase-server` would exec) and then supervises the
binary directly, so signals reach the member instead of a cargo wrapper.
No containers are used anywhere in this harness.

## What boots

| Piece | How | Endpoint |
| --- | --- | --- |
| GRU member | `terrabase-server --config .terrascale/stack/gru.toml` | api 7101 · tbep 8421 · obs 9461 |
| SFO member | `terrabase-server --config .terrascale/stack/sfo.toml` | api 7102 · tbep 8422 · obs 9462 |
| FRA member | `terrabase-server --config .terrascale/stack/fra.toml` | api 7103 · tbep 8423 · obs 9463 |
| Webhook receiver | `scripts/stack-webhook-receiver.py` (stdlib only) | 7090 (`GET /health`, `POST /hooks`) |
| REST Worker | `npm run dev` (`wrangler dev --env dev`) in `../ts-proxy` | 7000 |
| Dashboard | `npm run dev` (`vinext dev` on workerd) in `../ts-dashboard` | 7100 |

Members and the webhook receiver are the gate: `up` and `check` fail closed
unless all three members answer both probes below. The full `up` target
requires every G1 piece (members, REST Worker, dashboard, webhook
receiver, X-6 against the real member); a missing checkout, tool, or
health signal is an error, not a skip. The X-4 stub stays available only
through the separately named `pre-engine` target for engine work before
E-5/E-6 land, and is never a substitute for a member.

The proxy's `DEV_ORIGINS` (`../ts-proxy/wrangler.toml`) already points at
this stack's 7101/7102/7103, and the stub shares the local-edge harness's
insecure dev capability key, so the Snippet gate verifies end to end with
no extra configuration.

## Use

```sh
make stack          # boot everything, wait for health, verify
make stack-check    # probe a running stack (non-zero on any failure)
make stack-test     # run the real P-3 journey and X-6 qualification
make stack-down     # stop everything `up` started (state is kept)

./scripts/stack.sh logs            # tail every log
./scripts/stack.sh logs gru        # one service: gru|sfo|fra|proxy|dashboard|webhooks|stub
STACK_FRESH=1 make stack           # wipe .terrascale/stack (data, certs, configs) first
STACK_DRY_RUN=1 make stack         # print the plan without creating files or processes
```

State lives in `.terrascale/stack/`: per-region config + `engine/` +
`data/`, throwaway 3-day TLS material, the bootstrap API key, pid file,
per-service logs, and the webhook delivery log. Data survives restarts;
only `STACK_FRESH=1` wipes it.

## Health contract

Each member exposes two probes, and `check` requires both:

- Operator (unauthenticated): `GET http://127.0.0.1:9461/live`
  (also `/ready`, `/startup`, `/internal/metrics` on the same port).
- Public (bootstrap Bearer key): the key is minted once into
  `.terrascale/stack/bootstrap.key` (0600) and registered on every
  member with an all-verbs scope:

```sh
export STACK_BOOTSTRAP_KEY="$(cat .terrascale/stack/bootstrap.key)"
curl -H "Authorization: Bearer $STACK_BOOTSTRAP_KEY" \
  http://127.0.0.1:7101/api/v1/health
# {"region":"GRU","healthy":true}
```

The P-3 journey should read that file (or take the key from the
environment) and treat the three `710x` origins plus the `7090`
webhook receiver as its fixed topology.

## Dev TLS, and a known validator quirk

`ensure_tls` in `scripts/stack.sh` mints a throwaway CA + leaf (SAN
`IP:127.0.0.1,DNS:localhost`) with `openssl`. These files are dev-only:
3-day expiry, loopback SANs, never production, never shipped.

Quirk (engine-owned, not worked around by touching it): the member's PEM
gate rejects base64 body lines that mix `=` padding with other content,
so only DER lengths that are multiples of 3 (padding-free base64) are
admitted. The script therefore regenerates until `server.crt`,
`server.key`, and `ca.crt` contain no `=`. If the engine relaxes the
gate to accept padded PEM, the retry loop in `ensure_tls` can go away.

## Troubleshooting

- `stack up failed: members unhealthy` — tail the member logs:
  `./scripts/stack.sh logs gru`. Port collisions with a previous stack
  are the usual cause; `make stack-down` first.
- `Error: SecretFile` at boot — the TLS files tripped the member's PEM
  gate (see above); delete `.terrascale/stack/tls` and re-run.
- Proxy answers slowly on first boot — `wrangler dev` compiles the
  Worker; tail `logs/proxy.log` until `Ready on http://localhost:7000`.
- Dashboard exits with `TERRASCALE_API_BASE_URL is required` — fixed in
  the script (it exports the local proxy/public URLs); a bare
  `npm run dev` in `ts-dashboard` still needs them by hand.
- Dashboard refuses with "another vinext dev server is already running"
  — one `vinext dev` per checkout (lock at
  `ts-dashboard/.vinext/dev/lock.json`). Stop the other server first;
  the stack never kills a server it did not start.
- Dashboard worker env: the workerd runtime does **not** inherit the
  shell — the Cloudflare Vite plugin builds it from `wrangler.jsonc`
  vars plus `.dev.vars`. The script therefore writes a loopback-only
  `ts-dashboard/.dev.vars` (standard wrangler mechanism) with the local
  URLs, `NODE_ENV=development`, the local auth terms version, and a
  throwaway session secret. That file is stack-managed; dashboard owners
  may want `.dev.vars` in `ts-dashboard/.gitignore` (it is not there
  yet).
- Never expose this stack: no auth beyond the bootstrap key, insecure
  dev capability key on the stub, 3-day self-signed certs.
