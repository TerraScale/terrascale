# Fly spike 1a: platform facts (2026-10-11)

Measured on throwaway apps `spike-tb`, `spike-net` and `spike-pr-1` in org `terrascale`, regions gru and sjc,
flyctl v0.4.115. All three apps and their volumes were destroyed afterwards. Decisions live in
`.cos/fly-migration-plan.md`; this file records what the platform actually did.

## What ran

- **TerraBase**: the current monorepo `src/TerraBase` (main at 2026-10-11), published with
  `dotnet publish src/TerraBase -c Release -r linux-x64 --self-contained` (net11.0, single file, JIT, 125 MB)
  on `mcr.microsoft.com/dotnet/runtime-deps:10.0` (image 104 MB). It boots standalone: no NATS or peers
  needed. The monorepo targets **net11.0** (SDK 11.0.100-rc.1), not .NET 10.
- **Probe**: a throwaway Go binary for DNS, h2c, gRPC, fly-replay, SSE, `[[metrics]]` and memory-pressure
  tests. It also supervised TerraBase to time start to the first `200 /readyz` and read RSS, CPU and
  `dotnet_jit_*` from TerraBase's `/metrics`.

## Results

| # | Question | Result |
| --- | --- | --- |
| 1 | Boot, shared-cpu-2x 512 MB | Firecracker start ~1.2 s, then TerraBase ready in 3.86–3.91 s (sjc) and 3.94–4.04 s (gru). RSS 106 MB at ready, ~133 MB after 60 s of light load. The guest sees 459 MB `MemTotal`. |
| 1 | Swap (`swap_size_mb = 4096`) | Works: `SwapTotal` 4 GB. Swap is slow, about 4–7 MB/s. With 900 MB allocated next to TerraBase (2× RAM), there was no OOM kill, but `/readyz` took 1.5–3 s and spiked to 50 s while memory filled. Swap only prevents OOM kills; it is not usable memory. |
| 1 | TieredPGO on vs off | On 2 shared vCPUs, PGO costs +11–12% JIT time and +12–15% CPU in the first minute. Time to ready is unchanged. It is **not** the 2× that TS-607 saw on 1 vCPU. Steady-state gain was not measured. |
| 2 | Volume auto-extend | Accepted in `[mounts]`. It triggered within seconds at 83% (1 GB to 2 GB) and grew the ext4 filesystem online, with no restart. The minimum size is 1 GB (974 MB usable). Daily scheduled snapshots are on by default, with 5-day retention unless `snapshot_retention` is set (7 was accepted). |
| 3 | 6PN DNS | `<region>.<app>.internal`, `<app>.internal` (all started machines) and `top1.nearest.of.<app>.internal` all return AAAA. Stopped machines are absent. Same-region lookups take 1–3 ms. Names that include the other region take 180–350 ms uncached. `fly-local-6pn` is in `/etc/hosts`. |
| 3 | Listener on `fly-local-6pn` | Works: reachable from other machines over 6PN, refused on 127.0.0.1, not public. |
| 3 | h2c / gRPC gru↔sjc | Both work (HTTP/2.0 prior knowledge; gRPC health `SERVING`). |
| 3 | Latency gru↔sjc | 172 ms RTT (ping6 avg 172.4 ms; warm h2c request 172.6 ms). The first request takes 0.7–2.8 s (DNS + TCP + h2 setup). |
| 4 | `fly-replay` | `region=sjc` and `instance=<id>` from a gru machine both reach the target. The target gets `fly-replay-src: instance=<src>;region=gru;t=<µs>`. |
| 4 | Replay body limit | Bodies up to 10,485,759 bytes replay. At ≥ 10 MiB (10,485,760) the client gets **502** with an empty body. Requests that are not replayed accept 10 MiB. |
| 4 | SSE through Fly Proxy | A 7-minute stream with `auto_stop_machines = 'stop'` got all 28 events. The machine stopped 1 min 41 s after the stream closed. An open stream keeps the machine running. |
| 5 | Always-on per region | `auto_stop_machines = 'stop'` + `min_machines_running = 1` keeps only the **primary region** up: the sjc machine stopped after ~5 min idle. `auto_stop_machines = 'off'` kept 1 machine per region running for 30+ min, across deploys. |
| 6 | `[[metrics]]` | **The scraper can't reach a 6PN-only listener.** It connects over the machine's private IPv4 (`172.19.x.x`). A 6PN-bound port gave `up 0`; the same handler on all interfaces gave `up 1`, and the series appeared in `api.fly.io/prometheus/terrascale` (auth `Authorization: FlyV1 …` from `flyctl tokens create readonly terrascale`). The port was still not public. |
| 7 | Preview org | `flyctl apps create … -o terrascale-previews` fails: "We need your payment information to continue". The preview test ran in org `terrascale` instead. |
| 7 | Multi-container + volume | It works with the Fly Volume in the machine-level `mounts`, which mounts it into **every** container at the same path (shared, read-write both ways). Container-level `mounts` reject Fly Volumes: they only name entries in machine `volumes` (temp_dir/image). Containers share one network namespace, so loopback is shared, `/etc/hosts` maps each container name to 127.0.0.1, and `fly-local-6pn` is the same address in both. |
| 7 | Preview autostop/autostart | It stopped after idle and autostarted on the next request. The cold first request took 1.6 s (machine start 955 ms); warm requests take 79 ms. |
| 8 | Cost | About **$25.20/month** plus egress (see below). |

Other observations:

- **TerraBase refuses the volume root.** ext4 creates `lost+found`, so TerraBase reports a "pre-tenancy layout"
  and exits 2. Point it at a subdirectory: `/data/terrabase`.
- **gru CPUs are slower or more contended.** The same startup and first minute burned 4.2 s of CPU in gru
  vs 2.8 s in sjc, though wall time to ready differed by only ~0.1 s.
- **`fly deploy` does not restart a crash-looped machine.** A gru machine that hit the restart limit (exit 2
  above) stayed `stopped` after a `fly deploy` that fixed its config, and needed `flyctl machine start`. This
  was seen once.
- **Region-by-region rollout works.** `flyctl deploy --image <ref> --regions sjc` updated only the sjc
  machine.
- **First deploy creates 1 machine.** With `--ha=false` and a `[mounts]` section, the first deploy created 1
  machine and volume in the primary region. `flyctl scale count 1 --region sjc` then added the sjc machine
  and its volume. Later `fly deploy` runs kept exactly one machine per region.

## Raw PGO numbers (TerraBase, 4 restarts per cell)

| Region | PGO | Ready (s) | JIT time after +60 s (s) | Methods jitted | Process CPU after +60 s (s) | RSS +60 s (MB) |
| --- | --- | --- | --- | --- | --- | --- |
| sjc | off | 3.87 | 2.91 | ~8,100 | 2.78 | 133 |
| sjc | on | 3.88 | 3.22 | ~8,560 | 3.12 | 133 |
| gru | off | 3.99 | 3.73 | ~8,020 | 4.21 | 131 |
| gru | on | 3.99 | 4.19 | ~8,470 | 4.84 | 134 |

The load was 3,000 sequential `/readyz` requests with a `/metrics` scrape every 100th, then 60 s idle.
`DOTNET_TieredPGO` was toggled per machine. TerraBase.csproj currently sets `<TieredPGO>false</TieredPGO>`.

One unclean run is excluded. A restart that landed during the swap test logged 51 s of JIT time: under
memory pressure, startup JIT gets about 15× slower.

## Cost (Fly price list, 2026-10-11)

Prices are a base rate (iad/ewr = 1.0×) times a regional markup: gru 1.615×, sjc 1.192×. The base rate is
$0.69 per shared vCPU-month plus $6.00 per GB of RAM per month. That rate reproduces the published rows
(1x/256 MB $2.19, 1x/1 GB $6.70, 2x/2 GB $13.39).

| Item | gru | sjc | Total / month |
| --- | --- | --- | --- |
| TerraBase, shared-cpu-2x 512 MB, always on ($4.38 base) | $7.08 | $5.22 | $12.30 |
| ts-auth, same size | $7.08 | $5.22 | $12.30 |
| 4 volumes × 1 GB at $0.15/GB-month (not regional) | $0.30 | $0.30 | $0.60 |
| Daily snapshots (first 10 GB/month free) | | | $0 |
| Shared IPv4 + IPv6 | | | $0 |
| **Footprint** | | | **≈ $25.20** |

Usage-based costs on top of the footprint:

- **Internet egress**: $0.04/GB from South America, $0.02/GB from North America.
- **Private cross-region traffic** (replication): $0.015/GB from South America, $0.006/GB from North America.
- **Stopped machines**: $0.15/GB-month of rootfs.
- **Swap**: no line in the price list. Confirm on the first invoice.
- **TLS certificates**: not priced in this spike.

A preview machine (shared-cpu-1x 1 GB, $6.69 base) costs at most $0.36 per day running in gru, or $0.22 in
iad. When stopped, it costs only rootfs plus the 1 GB volume, under $0.01 per day.

## Working configs

### TerraBase (`ts-terrabase`)

```toml
app = 'ts-terrabase'
primary_region = 'gru'
swap_size_mb = 4096

[env]
  # The volume root holds lost+found; TerraBase needs an empty or own directory.
  TERRABASE_DATA_DIRECTORY = '/data/terrabase'

[mounts]
  source = 'data'
  destination = '/data'
  initial_size = '1gb'
  auto_extend_size_threshold = 80
  auto_extend_size_increment = '1gb'
  auto_extend_size_limit = '100gb'
  # daily snapshots are on by default, 5-day retention

[http_service]
  internal_port = 8080
  force_https = true
  auto_stop_machines = 'off'   # the only way to keep 1 machine running in EVERY region
  auto_start_machines = true
  min_machines_running = 0     # applies to the primary region only; irrelevant with 'off'
  [http_service.http_options]
    h2_backend = true          # accepted; h2c to the backend through the proxy was not verified

[[metrics]]
  port = 9091                  # must listen on 0.0.0.0 (IPv4 any), NOT fly-local-6pn
  path = '/metrics'

[[vm]]
  size = 'shared-cpu-2x'
  memory = '512mb'
```

In the spike, `TERRABASE_REGION=$FLY_REGION` was set by the entrypoint. The zero-config work should read
`FLY_REGION` directly. The internal port 8081 binds `fly-local-6pn` and needs no fly.toml entry.

Bring-up and rollout:

```sh
flyctl deploy -a ts-terrabase --remote-only --ha=false          # 1 machine + volume in gru
flyctl scale count 1 --region sjc -a ts-terrabase -y           # adds sjc machine + volume
# every release
flyctl deploy -a ts-terrabase --build-only --push --image-label "$SHA"
flyctl deploy -a ts-terrabase --image "registry.fly.io/ts-terrabase:$SHA" --regions gru
# live check gru, then
flyctl deploy -a ts-terrabase --image "registry.fly.io/ts-terrabase:$SHA" --regions sjc
```

### ts-auth (`ts-auth`)

This is the same shape, from the plan. Only the TerraBase variant was booted.

```toml
app = 'ts-auth'
primary_region = 'gru'
swap_size_mb = 4096

[mounts]
  source = 'data'
  destination = '/data'
  initial_size = '1gb'
  auto_extend_size_threshold = 80
  auto_extend_size_increment = '1gb'
  auto_extend_size_limit = '10gb'

[http_service]
  internal_port = 8080
  force_https = true
  auto_stop_machines = 'off'
  auto_start_machines = true
  min_machines_running = 0

[[metrics]]
  port = 9091                  # 0.0.0.0, see the [[metrics]] finding
  path = '/metrics'

[[vm]]
  size = 'shared-cpu-2x'
  memory = '512mb'
```

### PR preview machine (`ts-pr-<n>`)

The tested config is below. It used `spike-pr-1`, with the TerraBase image and the probe standing in for
ts-auth.

```sh
flyctl apps create "ts-pr-$N" -o terrascale-previews
flyctl deploy -a "ts-pr-$N" --build-only --push --image-label terrabase   # from the TerraBase Dockerfile
flyctl deploy -a "ts-pr-$N" --build-only --push --image-label auth        # from the ts-auth Dockerfile
VOL=$(flyctl volumes create data -s 1 -r gru -a "ts-pr-$N" -y --json | jq -r .id)
flyctl ips allocate-v6 -a "ts-pr-$N"; flyctl ips allocate-v4 --shared -a "ts-pr-$N"
flyctl machine run --machine-config machine.json -a "ts-pr-$N" -r gru   # machine.json below, $VOL substituted
# teardown
flyctl apps destroy "ts-pr-$N" -y
```

```json
{
  "guest": {"cpu_kind": "shared", "cpus": 1, "memory_mb": 1024},
  "mounts": [{"volume": "$VOL", "path": "/data", "name": "data"}],
  "containers": [
    {
      "name": "terrabase",
      "image": "registry.fly.io/ts-pr-$N:terrabase",
      "env": {"TERRABASE_DATA_DIRECTORY": "/data/terrabase"}
    },
    {
      "name": "auth",
      "image": "registry.fly.io/ts-pr-$N:auth",
      "depends_on": [{"name": "terrabase", "condition": "started"}]
    }
  ],
  "services": [{
    "protocol": "tcp", "internal_port": 8080,
    "autostop": "stop", "autostart": true, "min_machines_running": 0,
    "ports": [{"port": 443, "handlers": ["tls", "http"]}, {"port": 80, "handlers": ["http"]}]
  }]
}
```

Notes on the preview config:

- Do **not** put `mounts` on the containers: that fails with "container has mount data that is not
  specified as a volume".
- Only one container can own public port 8080. Here it is ts-auth. TerraBase's public port would need a
  second service on another port.
- `depends_on: healthy` needs a `healthchecks` entry on the TerraBase container. `started` was tested.
- The `ts-pr-<n>` naming in `.cos/fly-migration-plan.md` assumes the preview app is created in
  `terrascale-previews`, which still needs a credit card.

## Contradictions with the plan

1. **`[[metrics]]` cannot scrape a 6PN-only `/metrics`.** Bind the metrics port to `0.0.0.0` (IPv4 any).
   Fly's scraper reaches it over `172.19.x.x`. It is still not public, and with an IPv4-only bind it is not
   reachable over 6PN. This affects the plan's "/metrics on 6PN port 9091" (ts-auth) and "/metrics on the
   6PN 8081 listener" (TerraBase); TerraBase needs a separate metrics port (9091).
2. **ts-auth's constant `http://top1.nearest.of.ts-terrabase.internal:8081` does not resolve in a
   preview.** The preview app is `ts-pr-<n>` in another org with its own 6PN. In the preview machine,
   TerraBase is at `fly-local-6pn:8081` (same network namespace). The address must come from the
   environment: TerraBase is local when `FLY_APP_NAME` is `ts-pr-*`.
3. **Swap is not headroom.** At ~4–7 MB/s, anything that pages turns into multi-second latency. The memory
   budget must keep the working set under ~400 MB per machine, and swap only absorbs spikes.
4. **TieredPGO is cheap on 2 vCPUs.** It costs +12% JIT, not 2×. Enabling it per the plan is fine; the
   csproj still has it off.
5. **TerraBase cannot use the volume root.** `lost+found` trips its layout check. Use `/data/terrabase`, or
   have TerraBase ignore `lost+found`.
