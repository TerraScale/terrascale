# Grafana on fly-metrics.net

Dashboards and alert rules for the `terrascale` Fly org, as code. Observability is Fly's managed Prometheus +
Grafana only (`.cos/fly-migration-plan.md`): no Elasticsearch, no OTLP collector.

| File | What |
| --- | --- |
| `grafana.py` | Generates the JSON, checks every query against Fly's Prometheus, provisions Grafana |
| `dashboards/overview.json` | TerraScale overview: scrape health, traffic, errors, latency, replication, backups, machines |
| `dashboards/terrabase.json` | TerraBase: requests per route and region, replication, storage, backups, .NET runtime, machine |
| `dashboards/ts-auth.json` | ts-auth: requests per route and region, sign-ins, tokens, email, TerraBase calls, runtime, machine |
| `alerts.json` | Grafana-managed alert rules (table below) |

Edit `grafana.py`, never the JSON, then:

```sh
python3 ops/grafana/grafana.py build   # rewrites dashboards/*.json and alerts.json
python3 ops/grafana/grafana.py check   # runs every query on api.fly.io/prometheus/terrascale (needs flyctl login)
```

`check` prints `data` or `empty` per query and fails on any query Prometheus rejects. `empty` is expected for
series a service does not emit yet (see "Metrics" below).

## Where the data comes from

- Fly scrapes each machine's `[[metrics]]` port every 15 s and adds `app`, `region`, `instance` and `host` to every
  series, so code never adds a region label. The scraper connects over the machine's private IPv4: the port must
  listen on all interfaces (`docs/fly/spike-1a.md`, finding 6) and must never be a `[[services]]` port.
  TerraBase: `9091`; ts-auth: `9092`.
- Fly's own `fly_instance_*` series (memory, swap, CPU, CPU burst balance, PSI, network, filesystem) exist for every
  machine without any code.

## Install the dashboards

fly-metrics.net only accepts its own Fly OAuth login: Fly-issued tokens (`flyctl tokens create readonly`,
`flyctl auth token`) get 401 on its HTTP API (tested 2026-10-11). Two ways in:

**One-click import (no token).**

1. Open https://fly-metrics.net, sign in with Fly, pick the `terrascale` org.
2. Dashboards → New → Import → upload `dashboards/overview.json` → choose the Prometheus data source → Import.
3. Repeat for `terrabase.json` and `ts-auth.json`. Re-importing the same file overwrites it (stable `uid`).

**API provisioning (dashboards + alerts + contact point).** Needs a Grafana service-account token, which only a
Grafana org admin can create (Administration → Users and access → Service accounts → Add, role Editor → Add token).

```sh
install -m 600 /dev/null ~/.config/terrascale/grafana-token   # paste the token into this file
GRAFANA_TOKEN_FILE=~/.config/terrascale/grafana-token python3 ops/grafana/grafana.py provision --email admin@terrascale.tech
```

It creates the `TerraScale` folder, the three dashboards, an email contact point and the `terrascale` rule group
(evaluated every 60 s; the group is replaced whole, so rules deleted from `alerts.json` disappear). The token is
read from the file and never printed.

## Alerts

| Rule | Fires when | For |
| --- | --- | --- |
| Replication send lag > 30 s | `terrabase_replication_send_lag_seconds` per region → peer | 5 min |
| Backup shipping lag > 10 min | `terrabase_backup_lag_seconds` per region | 10 min |
| 5xx ratio > 5% | 5xx / all requests per app and region, only above 0.05 req/s | 5 min |
| Memory available < 10% | `fly_instance_memory_mem_available / mem_total` per app and region | 10 min |
| Swap used > 256 MB | `swap_total - swap_free` per app and region | 15 min |
| Metrics scrape failing | `up < 1` per app and region (no data also alerts) | 5 min |

Missing series count as OK except for the scrape rule, so rules on metrics a service does not emit yet stay quiet.

Routing: every rule names the `TerraScale owner` contact point (Grafana email to the `--email` address). Grafana
sends from Fly's own mail setup; it cannot send from `no-reply@terrascale.tech`. Mail from that address through
Cloudflare Email Sending would need a webhook contact point and a small receiver (for example in ts-status), which
does not exist.

Grafana alerting availability on fly-metrics.net is not verified: the API refuses Fly tokens (above). If the
alerting pages are missing after sign-in, the rules in `alerts.json` stay the reference and nothing alerts.

## Metrics

Prometheus names, as the OpenTelemetry exporter writes them (dots become `_`, unit `s` adds `_seconds`, counters add
`_total`). Labels have fixed small value sets; never put IDs, paths with IDs, emails or tenants in a label.

**Both services:** `http_server_request_duration_seconds{http_route, http_request_method, http_response_status_code}`
(rate, errors, latency per route), `dotnet_gc_last_collection_heap_size_bytes`, `dotnet_process_memory_working_set_bytes`,
`dotnet_process_cpu_time_seconds_total`, `dotnet_thread_pool_queue_length`, `dotnet_thread_pool_thread_count`,
`dotnet_gc_pause_time_seconds_total`, `dotnet_gc_collections_total`.

**ts-auth:** `ts_auth_sign_ins_total{method, outcome}`, `ts_auth_tokens_total{grant_type, outcome}`,
`ts_auth_emails_total{outcome}` (docs/auth/README.md), `http_client_request_duration_seconds` (calls to TerraBase and
Cloudflare).

**TerraBase, served today:** `terrabase_pressure_data_bytes`, `terrabase_pressure_disk_used_bytes`,
`terrabase_pressure_disk_total_bytes`, `terrabase_stats_databases`, `terrabase_backup_lag_seconds`,
`terrabase_backup_last_success_seconds`, `terrabase_backup_failures_total`.

**TerraBase replication (design C), emitted by the slice that builds the part.** The dashboards and alerts already
query these names; a slice that picks another name updates `grafana.py` in the same PR. Add each to the cardinality
list in `src/TerraBase/Hosting/Telemetry.cs` (`peer_region` has one value per region).

| Slice | Instrument | Kind, unit | Labels | Meaning |
| --- | --- | --- | --- | --- |
| R3 | `terrabase.replication.cdc_backlog` | gauge, no unit | — | CDC rows kept because some peer has not acked them, summed over databases as of each one's last ack. R3 builds groups from `turso_cdc` on demand, so there is no tailer pass and no tailer lag: `send_lag` is the age of what is not shipped |
| R4a | `terrabase.replication.send_lag` | gauge, `s` | `peer_region` | Age of the oldest change group not yet sent to the peer |
| R4a | `terrabase.replication.ack_lag` | gauge, `s` | `peer_region` | Age of the oldest change group sent but not acked |
| R4a | `terrabase.replication.apply_delay` | histogram, `s`, buckets 0.05 … 300 | `peer_region` (origin) | Apply time minus the origin stamp's physical time: convergence lag |
| R4a | `terrabase.replication.merged_groups` | counter | `peer_region` (origin) | Change groups joined |
| R6 | `terrabase.replication.repairs` | counter | `peer_region` | Documents changed by anti-entropy |
| R4a | `terrabase.clock.skew_capped` | counter | — | Stamps capped at now + 1 s (section 3 of the design) |

Backup shipping (phase 3b) keeps `terrabase_backup_lag_seconds` and `terrabase_backup_last_success_seconds`.

## Verified

2026-10-11, `grafana.py check` against the live org: all 102 queries (panels + alerts) parse and run. Fly machine series return data.
TerraBase and ts-auth app series are empty: the gru TerraBase machine (old image) serves `/metrics` on loopback only, so
`up{app="ts-terrabase",region="gru"}` is 0, which is exactly what the scrape alert reports. ts-auth has no machine yet.
