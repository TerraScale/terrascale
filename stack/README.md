# Local platform stack

One command runs TerraBase, ts-auth, the dashboard (live mode) and Mailpit on
this machine, over HTTPS with a stack-local CA:

```sh
stack/bin/stack up        # build, bootstrap, start, wait until healthy
stack/bin/stack check     # probe every service
stack/bin/stack down
```

| Service   | Listens on                    | Public URL                    |
|-----------|-------------------------------|-------------------------------|
| TerraBase | admin `127.0.0.1:9464` (real backend), client TLS `127.0.0.1:9465`, edge TLS `127.0.0.1:9466` (fake backend) | `https://127.0.0.1:9465` |
| Mailpit   | SMTP `127.0.0.1:1025` (plaintext from ts-auth; STARTTLS offered), UI `127.0.0.1:8025` | `http://127.0.0.1:8025` |
| ts-auth   | `127.0.0.1:18455`             | `https://localhost:18445` (Caddy) |
| dashboard | `127.0.0.1:18456`             | `https://localhost:18446` (Caddy) |

`STACK_PORT_OFFSET=N` adds `N` to every port. Each worktree has its own
`.stack/`, so give each worktree its own offset in steps of 100 (`up` refuses
to start when a port is taken). The offset, backend and preview host are saved
in `.stack/stack.conf`, so later commands reuse them.

## Tools

`stack/bin/stack` runs itself under `mise exec` with `stack/mise.toml`
(process-compose, Caddy, mkcert, Mailpit, jq, shellcheck); mise installs
missing versions on first use. Without mise, put those tools on `PATH`.
Building from source also needs Go, Node/npm (dashboard) and Cargo (ts-auth,
TerraBase).

## Commands

| Command | What it does |
|---------|--------------|
| `up [process...]` | Create certificates and secrets, build missing binaries, start process-compose detached, wait for health, report. With process names, start only those and their dependencies (`up mailpit caddy`). |
| `down` | Stop every process. State stays in `.stack/`. |
| `reset` | `down`, remove the preview rules, delete `.stack/` except the build cache (`.stack/bin`, `.stack/target`). |
| `env [--export\|--path]` | Show `.stack/env` with secrets masked; `--export` prints `export` lines for `eval`. |
| `check` | TerraBase readiness (admin `/readyz`, or `/v1/readyz` on the fake) and an authenticated control-plane call, OIDC discovery and JWKS at the issuer, dashboard `/health/ready` through Caddy, Mailpit API and SMTP. Exits non-zero on any failure. |
| `bootstrap` | Re-run `auth-app provision`, `dashboard migrate` and `auth-app bootstrap-platform` against the running stack, then rewrite `.stack/env`. |
| `preview [off]` | Publish ts-auth and the dashboard on the tailnet with `tailscale serve` (ports `18545`/`18546` plus the offset) and restart the stack with those URLs as issuer, redirect URIs and cookie origin. `preview off` removes the rules. |
| `status`, `logs <process> [-f]` | Process table; one process's log from `.stack/logs/`. |
| `build [tb\|auth\|dashboard]` | Build binaries into `.stack/bin` without starting anything. |
| `config` | Validate `process-compose.yaml` and the Caddyfile. |

## Sources and binaries

By default the stack builds from sibling checkouts: `STACK_SRC` defaults to
the directory holding the main `ts-dashboard` checkout (so `../ts-auth`,
`../terrabase`, `../ts-go`). The dashboard is built from the checkout that
contains this `stack/` directory. Override one repository with `TS_AUTH_SRC`,
`TERRABASE_SRC`, `TS_GO_SRC` or `DASHBOARD_SRC`, or skip a build with a
prebuilt binary:

| Variable | Binary |
|----------|--------|
| `TB_SERVER_BIN`, `TB_INIT_BIN` | `tb-server`, `terrabase-init` (real backend) |
| `TBV1_FAKE_BIN` | `tbv1-fake` (fake backend) |
| `AUTH_APP_BIN` | `auth-app` |
| `DASHBOARD_BIN` | `dashboard` |

Builds never write into the source checkouts: Cargo uses
`CARGO_TARGET_DIR=.stack/target/<repo>` with `--locked`, Go writes to
`.stack/bin`. ts-auth depends on `../ts-rust` by path at the revision in
`ts-auth/.forgejo/workflows/ci.yml` (`TS_RUST_REV`); if the sibling `ts-rust`
is elsewhere, `--locked` fails. Point `TS_AUTH_SRC` at a ts-auth checkout
whose `../ts-rust` is at that revision, for example two detached worktrees in
one temporary directory. `STACK_SKIP_BUILD=1` uses whatever is in
`.stack/bin`.

## Backends

`TB_BACKEND=fake` (default) runs `tbv1-fake` from ts-go `cmd/tbv1-fake`
(branch `feat/v1-fake` until it merges): `--listen 127.0.0.1:9465
--edge-listen 127.0.0.1:9466 --cert-dir .stack/tb/certs --control-key-out
.stack/tb/control-key`, healthy when `GET /v1/readyz` answers 200 over TLS.
The certificate directory holds the stack CA and leaf as `ca.pem`,
`server.pem` and `server-key.pem`, so the fake serves the same chain as Caddy
and `TS_TERRABASE_CA_FILE` points at `ca.pem`. The fake keeps state in memory:
every start issues a new control key and empties every tenant. It is not
tb-server; acceptance runs use `TB_BACKEND=real`.

`TB_BACKEND=real` runs `terrabase-init --data-dir .stack/tb/data
--bootstrap-control-key --control-key-out .stack/tb/control-key` once, then
`tb-server --data-dir .stack/tb/data --port 9464 --client-config
.stack/tb/client.json`. `TB_INIT_ARGS` and `TB_SERVER_ARGS` append flags;
`TB_MEMORY_LIMIT_BYTES` and `TB_MEMORY_HEADROOM_BYTES` size the server. The
client config uses the stack certificate on `127.0.0.1:9465`. The control
plane is described in terrabase `docs/control-plane-v1.md`; the local
startup contract is `docs/local-dev.md` on `feat/v1-openapi-seams`.

## Bootstrap order

process-compose (`stack/process-compose.yaml`) enforces it. The stack only
orchestrates; every TerraBase call goes through the owning service's own
subcommand.

1. `up`: mkcert CA and `localhost`/`127.0.0.1`/`::1` certificate in
   `.stack/ca` and `.stack/certs` (no system trust store changes), ts-auth
   master, audit-signing, operator and metrics keys in `.stack/secrets`
   (0600, reused across restarts).
2. `mailpit`, `caddy`.
3. `tb-init` (real backend) → `tb-server` healthy → `stack-env` writes
   `.stack/env`.
4. `auth-provision`: `auth-app provision
   --control-key-file=.stack/tb/control-key --output=.stack/auth-provision.json`
   with `TS_AUTH_TERRABASE_URL` and `TS_AUTH_TERRABASE_CA_FILE`. Output:
   `{terrabase_url, tenant_id, database, data_key}`.
5. `dashboard-migrate`: `dashboard migrate` with the dashboard environment
   below (needs only TerraBase, so it runs beside step 4).
6. `ts-auth`: `Development` environment, `TS_AUTH_TERRABASE_{URL,CA_FILE,KEY}`,
   `TS_AUTH_TERRASCALE_TENANT_ID` and `TS_AUTH_DATABASE` from step 4, and
   plaintext SMTP to Mailpit (`TS_AUTH_SMTP_PLAINTEXT=true`, host
   `127.0.0.1`, no credentials; ts-auth allows this only in Development to a
   loopback host).
7. `auth-bootstrap-platform`: `auth-app bootstrap-platform
   --redirect-uri=<dashboard>/auth/callback
   --post-logout-redirect-uri=<dashboard>/ --output=.stack/auth-platform.json`.
   Output: `{issuer, instance_id, client_id, client_secret?, management_key?}`.

ts-auth's CLI takes `--key=value` arguments only. Secrets (`data_key`,
`client_secret`, `management_key`) are returned only by the run that creates
them, so the stack moves them out of the JSON files into `.stack/secrets`
(0600) and reuses them on later runs; a newer value replaces the saved one. A
step fails if it gets no secret and none is saved (`stack reset` starts over).
8. `dashboard` with `TS_DASHBOARD_LIVE=1`.

A step whose subcommand does not exist yet exits with `stack: waiting on
<workstream>: …` (exit 3); process-compose then marks everything that depends
on it `Skipped`, and `up` exits non-zero after printing the step's log. Nothing
is skipped silently, and live mode never falls back to demo data.

| Step | Waits on |
|------|----------|
| `tbv1-fake` | GO-b (ts-go `feat/v1-fake`) |
| `terrabase-init --bootstrap-control-key` | TB-1 (terrabase `feat/v1-openapi-seams`) |
| `auth-app provision` | AUTH-1c |
| `auth-app bootstrap-platform` | AUTH-2a |
| `dashboard migrate`, live config | DASH-1a, DASH-0 |

## The `.stack/env` contract

`.stack/env` is written with mode 0600, is gitignored, and holds one
`KEY=value` per line. Values containing spaces (a checkout path with a space)
are single-quoted, `KEY='value'`; values never contain quotes, `$`, backslashes
or newlines. Values not
available yet appear as `# KEY= (waiting on …)` comments. Load it with
`set -a; . .stack/env; set +a` or `eval "$(stack/bin/stack env --export)"`.

| Key | Value |
|-----|-------|
| `TS_TERRABASE_URL` | TerraBase client API, `https://127.0.0.1:9465` |
| `TS_TERRABASE_CA_FILE` | CA for that listener |
| `TS_TERRABASE_CONTROL_KEY` | Bootstrap control-role key |
| `TB_V1_URL`, `TB_V1_CA_FILE`, `TB_V1_CONTROL_KEY` | Aliases for the SDK live suites |
| `TS_AUTH_URL` | ts-auth public origin |
| `TS_AUTH_ISSUER` | Platform OIDC issuer (from `bootstrap-platform`) |
| `TS_AUTH_CA_FILE` | CA for the ts-auth origin |
| `TS_AUTH_CLIENT_ID`, `TS_AUTH_CLIENT_SECRET` | Dashboard confidential client |
| `TS_AUTH_OPERATOR_KEY` | ts-auth operator key |
| `MAILPIT_URL`, `MAILPIT_SMTP_ADDR` | Mailpit UI/API and SMTP |
| `DASHBOARD_URL` | Dashboard public URL |
| `STACK_CA_FILE` | Stack CA (Caddy and Mailpit) |

The dashboard process receives the same `TS_*` values plus
`TS_DASHBOARD_LIVE=1`, `DASHBOARD_ADDR`, `DASHBOARD_PUBLIC_URL` and
`DASHBOARD_SECURE_COOKIES=1`.

## Trusting the CA in a browser

The CA lives only in `.stack/ca`. Either use `stack preview` (tailnet
certificates are publicly trusted) or install it explicitly:
`CAROOT=.stack/ca mkcert -install`.
