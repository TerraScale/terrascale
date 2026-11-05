# Organization plans in every region: shipping and live proof

TerraBase now publishes each organization plan to every discovered region. An unknown plan returns `503 plan_unknown` instead of silently admitting a paid organization as Hobby. Dashboard organization setup waits for successful publication in every region before it returns success, including an explicit Hobby publication for Free signup.

The regional-plan implementation shipped through `ts-ship` as **06892e828807ee5d482f7ba8eca04f74c7deb43b**. The synchronous signup publication barrier shipped as **8082a063e2f3752151ecc6494c28839905a63f84**. No router or replication implementation changed in this task.

## Plan enforcement

The same synthetic organization `01M6NP7QGXD11EGWMMYAHVGQ2G`, project `01M6NP7QJPBPXKVQ8K3ZMX1Q0Q`, and database `01M6NPEF0BE1VN9A1CMFBQYXQA` were used for the Hobby/Pro comparison. The database was homed in GRU and available in SJC/FRA. The account was created with `ts-throwaway-account regional-plan-sol-1105` as Free; the authorized internal organization-plan API at GRU temporarily published Pro. No subscription was purchased. Dashboard remained the billing source and therefore periodically restored this synthetic account's real Free entitlement.

`QosPolicy` configures Pro at **500 requests/s, burst 1000**, versus Hobby at **50 requests/s, burst 100**. These are configured limits, not measured burst sizes. The table reports measured accepted HTTP 200 responses per second. Short-window rates can exceed 500 because the bucket permits a burst.

The workload ran on a dev host through `ts-heavy-any`, with 128 parallel h2c curl requests, a 3000-request warmup, and 6000 requests per measured window. It exchanged the real API key for a project JWT. A peer credential carried the forwarded client request to the internal listener, where the real organization admission path resolved the JWT's organization. This isolates regional admission from the public load balancer; it is not a public-edge throughput benchmark. Every response was checked for the requested `X-TerraBase-Region`; SQL reads carrying home-forward timing were rejected. The SJC/FRA machines served their own reads.

### Three measured windows per plan and region

| Plan | Region | Window | Seconds | HTTP 200 | HTTP 429 | Accepted/s | Live TerraBase revision |
| --- | --- | ---: | ---: | ---: | ---: | ---: | --- |
| hobby | sjc | 1 | 10.386 | 525 | 5475 | 50.55 | `06892e828807ee5d482f7ba8eca04f74c7deb43b` |
| hobby | sjc | 2 | 10.689 | 543 | 5457 | 50.80 | `06892e828807ee5d482f7ba8eca04f74c7deb43b` |
| hobby | sjc | 3 | 10.392 | 529 | 5471 | 50.90 | `06892e828807ee5d482f7ba8eca04f74c7deb43b` |
| hobby | fra | 1 | 11.024 | 557 | 5443 | 50.53 | `06892e828807ee5d482f7ba8eca04f74c7deb43b` |
| hobby | fra | 2 | 10.911 | 554 | 5446 | 50.77 | `06892e828807ee5d482f7ba8eca04f74c7deb43b` |
| hobby | fra | 3 | 11.072 | 562 | 5438 | 50.76 | `06892e828807ee5d482f7ba8eca04f74c7deb43b` |
| pro | sjc | 1 | 10.473 | 5756 | 244 | 549.59 | `d450cbe3b439b79f3b9710e1d7fe11e03e1a50d7` |
| pro | sjc | 2 | 10.401 | 5378 | 622 | 517.05 | `d450cbe3b439b79f3b9710e1d7fe11e03e1a50d7` |
| pro | sjc | 3 | 10.554 | 5482 | 518 | 519.44 | `d450cbe3b439b79f3b9710e1d7fe11e03e1a50d7` |
| pro | fra | 1 | 11.149 | 5784 | 216 | 518.80 | `06892e828807ee5d482f7ba8eca04f74c7deb43b` |
| pro | fra | 2 | 11.355 | 5953 | 47 | 524.27 | `06892e828807ee5d482f7ba8eca04f74c7deb43b` |
| pro | fra | 1 | 11.111 | 5811 | 189 | 523.00 | `caf93a56825b8668bcc01ae476245052f461506d` |

The three FRA Pro windows are independent observations across two runs, not three consecutive passes of the final driver. The first two were collected on 06892e8; the third was collected on caf93a5, a descendant containing the same regional-plan implementation. SJC's three comparison windows were collected on d450cbe, also a descendant. Each Pro window accepted more requests than Hobby could admit even with a completely full bucket (`100 + 50 × elapsed`), and stayed within the configured Pro bucket bound (`1000 + 500 × elapsed`). All observed rejections were `429 tenant_busy`.

The final FRA 523.00/s window also recorded QoS counter deltas: 5811 completed Pro requests, 189 Pro concurrency rejections, and no Hobby increments in the observed tenant bucket. These counters have 64 shared tenant buckets, so they corroborate the per-response proof without uniquely identifying the organization. They are not a separate throughput measurement.

### Interrupted windows and failed attempts

The initial FRA third Pro window fell to 120.23/s. A later FRA window fell to 115.06/s. The final instrumented sequence contained a 356.40/s window with 3742 Pro and 284 Hobby completions, plus Hobby rate/concurrency rejections. These were not counted as uninterrupted Pro comparisons: the synthetic account's Free billing publisher changed the admission plan during the measurement. The first driver only required admission above the Hobby upper bound, which was insufficient to detect every mid-window downgrade; later drivers added a 400/s minimum and checked QoS counter changes.

Repeated publication under load also encountered a non-2xx plan response; the managed run stopped without producing three measured windows. A later lightweight publication at GRU returned 200 with `plan=pro`. The two longer FRA sequences were stopped after repeated plan changes. No benchmark was rerun after the comparison observations above were recorded. The records below retain every emitted measured window, including the interrupted ones, rather than presenting a continuous three-pass FRA run.

| Attempt | Plan | Region | Window | Seconds | HTTP 200 | HTTP 429 | Accepted/s | Live revision |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | --- |
| initial | hobby | sjc | 1 | 10.386 | 525 | 5475 | 50.55 | `06892e828807ee5d482f7ba8eca04f74c7deb43b` |
| initial | hobby | sjc | 2 | 10.689 | 543 | 5457 | 50.80 | `06892e828807ee5d482f7ba8eca04f74c7deb43b` |
| initial | hobby | sjc | 3 | 10.392 | 529 | 5471 | 50.90 | `06892e828807ee5d482f7ba8eca04f74c7deb43b` |
| initial | hobby | fra | 1 | 11.024 | 557 | 5443 | 50.53 | `06892e828807ee5d482f7ba8eca04f74c7deb43b` |
| initial | hobby | fra | 2 | 10.911 | 554 | 5446 | 50.77 | `06892e828807ee5d482f7ba8eca04f74c7deb43b` |
| initial | hobby | fra | 3 | 11.072 | 562 | 5438 | 50.76 | `06892e828807ee5d482f7ba8eca04f74c7deb43b` |
| initial | pro | sjc | 1 | 10.481 | 5749 | 251 | 548.51 | `06892e828807ee5d482f7ba8eca04f74c7deb43b` |
| initial | pro | sjc | 2 | 10.403 | 5257 | 743 | 505.32 | `06892e828807ee5d482f7ba8eca04f74c7deb43b` |
| initial | pro | sjc | 3 | 10.541 | 5327 | 673 | 505.36 | `06892e828807ee5d482f7ba8eca04f74c7deb43b` |
| initial | pro | fra | 1 | 11.149 | 5784 | 216 | 518.80 | `06892e828807ee5d482f7ba8eca04f74c7deb43b` |
| initial | pro | fra | 2 | 11.355 | 5953 | 47 | 524.27 | `06892e828807ee5d482f7ba8eca04f74c7deb43b` |
| initial | pro | fra | 3 | 11.096 | 1334 | 4666 | 120.23 | `06892e828807ee5d482f7ba8eca04f74c7deb43b` |
| republish-per-window | pro | sjc | 1 | 10.473 | 5756 | 244 | 549.59 | `d450cbe3b439b79f3b9710e1d7fe11e03e1a50d7` |
| republish-per-window | pro | sjc | 2 | 10.401 | 5378 | 622 | 517.05 | `d450cbe3b439b79f3b9710e1d7fe11e03e1a50d7` |
| republish-per-window | pro | sjc | 3 | 10.554 | 5482 | 518 | 519.44 | `d450cbe3b439b79f3b9710e1d7fe11e03e1a50d7` |
| republish-per-window | pro | fra | 1 | 10.916 | 1256 | 4744 | 115.06 | `d450cbe3b439b79f3b9710e1d7fe11e03e1a50d7` |
| single-publication + QoS | pro | fra | 1 | 11.111 | 5811 | 189 | 523.00 | `caf93a56825b8668bcc01ae476245052f461506d` |
| single-publication + QoS | pro | fra | 2 | 11.296 | 4026 | 1974 | 356.40 | `caf93a56825b8668bcc01ae476245052f461506d` |

## Brand-new signup: first create and regional writes

Dashboard `/health/live` reported exactly **8082a063e2f3752151ecc6494c28839905a63f84** before both signups. TerraBase reported **caf93a56825b8668bcc01ae476245052f461506d**, which contains the original plan change. Each probe created a genuinely new account and key, exchanged the key for a JWT, then immediately issued its first database request. No plan was published manually, and no database/write request was retried. Elapsed time starts when `ts-throwaway-account` returned the ready key-file path, after normal signup/onboarding.

Each fresh organization successfully wrote through both regions. Two separate fresh signups tested the first-create ingress independently, preserving Free's one-live-database allowance.

| Signup | Operation | Database home | Ingress | Response region | HTTP | Seconds after ready | Rows affected |
| --- | --- | --- | --- | --- | ---: | ---: | ---: |
| new SJC | create | sjc | sjc | sjc | 201 | 0.884 | — |
| new SJC | write | sjc | sjc | sjc | 200 | 1.504 | 1 |
| new SJC | write | sjc | fra | sjc | 200 | 2.612 | 1 |
| new FRA | create | fra | fra | fra | 201 | 0.926 | — |
| new FRA | write | fra | sjc | fra | 200 | 2.634 | 1 |
| new FRA | write | fra | fra | fra | 200 | 3.084 | 1 |

No `plan_unknown` response occurred. The write batches included `CREATE TABLE IF NOT EXISTS`, so the off-home writes correctly forwarded to the database home with `route=home; reason=ddl; commit=home`. They demonstrate successful admission and writes through both ingresses; they do not claim local off-home DDL execution. The paid-plan read measurements above separately verify local SJC/FRA execution.

The first signup also attempted a second database create at FRA immediately after deleting its SJC database (DELETE 204). It returned `409 database_limit` at 3.740 s, rather than `plan_unknown`. This prevented treating that extra create as a successful second-region create in the same Free account. The isolated FRA signup then proved its first create independently. Parked: [#1098 — Free database allowance remains occupied after a cross-region delete returns 204](https://github.com/TerraScale/terrascale/issues/1098). The router and replication paths were left to their owners.

The retained Free billing journey covers refused initial publication: setup returns 503, retry adopts the same organization, and only a successful explicit Hobby publication permits onboarding to finish. Auth and payment producer fixtures now implement the required credentialed plan route; retained assertions were preserved.

## Verification and rollout

| Proof | Source | Result |
| --- | --- | --- |
| TerraBase Fast | 06892e828807ee5d482f7ba8eca04f74c7deb43b | 59/59; final shipping runner 12.756 s |
| Auth Fast | 06892e828807ee5d482f7ba8eca04f74c7deb43b | 30/30; 9.676 s |
| Dashboard Fast after signup barrier | 8082a063e2f3752151ecc6494c28839905a63f84 | 103/103; final shipping runner 9.592 s; slowest test 4.057 s |
| R13 independent invocation 1 | 06892e828807ee5d482f7ba8eca04f74c7deb43b | passed; 311.277 s |
| R13 independent invocation 2 | same | passed; 339.960 s |
| R13 independent invocation 3 | same | passed; 342.431 s |
| R13 independent invocation 4 | same | passed; 346.098 s |
| R13 independent invocation 5 | same | passed; 351.658 s |

The R13 fixture now mints a 30-minute JWT. Its previous five-minute JWT expired while the test was still running (`401 invalid_key`); the Retry-After workaround is removed and assertions remain intact. One managed `ts-heavy-any` job built once, then ran five independent invocations with default parallelism. [#1088](https://github.com/TerraScale/terrascale/issues/1088) is closed with the proof. No Slow test was skipped or weakened.

The original implementation was observed live in GRU/SJC/FRA at 06892e8, image `sha256:7cbd16a61af065243a28848fc86201006a2d28b570e9ebc1ab420c91bb82605e`. Subsequent measured revisions and images are retained in the event appendix. For the signup barrier, [Dashboard Fast](https://github.com/TerraScale/terrascale/actions/runs/42038971140/job/127284423190), [CI/test](https://github.com/TerraScale/terrascale/actions/runs/42038971263/job/127284618102), and [Images ready](https://github.com/TerraScale/terrascale/actions/runs/42039087999/job/127284825050) succeeded before the live signup proof. The `ts-main` snapshot retained unrelated older Slow probe failures and missing SDK/landing history; these are separate from the changed area's green proof. The final follow-up snapshot is included below.

## Cleanup

The paid-proof database was deleted with HTTP 204. Both fresh-signup databases were deleted with HTTP 204. All three accounts (`regional-plan-sol-1105`, `regional-plan-new-1105`, `regional-plan-new-fra-1105`) were deleted with `ts-throwaway-account --delete TAG`. Each initial account deletion entered the service's pending-deletion state; a subsequent cleanup invocation completed it. Those cleanup retries did not retry any proof request. The helper removed each private state/key file. Temporary credential headers were scoped to private temporary directories and removed. No credentials are included here.

## Redacted events

This appendix retains the numeric proof, local response metadata, plan-counter changes, and cleanup outcomes. It omits credentials, account state, and build chatter.

```json
[
  {
    "attempt": "initial",
    "removed": 0,
    "kept_live": 4,
    "freed_bytes": 0,
    "free_percent": 58.71
  },
  {
    "attempt": "initial",
    "event": "published",
    "region": "gru",
    "organization": "01M6NP7QGXD11EGWMMYAHVGQ2G",
    "plan": "hobby",
    "revision": "06892e828807ee5d482f7ba8eca04f74c7deb43b"
  },
  {
    "attempt": "initial",
    "event": "ready",
    "region": "gru",
    "revision": "06892e828807ee5d482f7ba8eca04f74c7deb43b",
    "sql_status": 200,
    "sql_region": "gru",
    "sql_machine": "ts-gru-1",
    "image": "ghcr.io/terrascale/terrabase@sha256:7cbd16a61af065243a28848fc86201006a2d28b570e9ebc1ab420c91bb82605e"
  },
  {
    "attempt": "initial",
    "event": "ready",
    "region": "sjc",
    "revision": "06892e828807ee5d482f7ba8eca04f74c7deb43b",
    "sql_status": 200,
    "sql_region": "sjc",
    "sql_machine": "ts-sjc-1",
    "image": "ghcr.io/terrascale/terrabase@sha256:7cbd16a61af065243a28848fc86201006a2d28b570e9ebc1ab420c91bb82605e"
  },
  {
    "attempt": "initial",
    "event": "warmup",
    "plan": "hobby",
    "requests": 3000,
    "parallel": 128,
    "repeat": 0,
    "region": "sjc",
    "seconds": 5.537,
    "http_statuses": {
      "200": 355,
      "429": 2645
    },
    "errors": {
      "tenant_busy": 2645
    },
    "accepted_per_second": 64.11,
    "hobby_max_in_window": 376,
    "exceeds_hobby": false,
    "configured_requests_per_second": 50,
    "configured_burst": 100,
    "plan_max_in_window": 377,
    "verified_local_responses": 3000,
    "revision": "06892e828807ee5d482f7ba8eca04f74c7deb43b"
  },
  {
    "attempt": "initial",
    "event": "measured",
    "plan": "hobby",
    "requests": 6000,
    "parallel": 128,
    "repeat": 1,
    "region": "sjc",
    "seconds": 10.386,
    "http_statuses": {
      "200": 525,
      "429": 5475
    },
    "errors": {
      "tenant_busy": 5475
    },
    "accepted_per_second": 50.55,
    "hobby_max_in_window": 619,
    "exceeds_hobby": false,
    "configured_requests_per_second": 50,
    "configured_burst": 100,
    "plan_max_in_window": 620,
    "verified_local_responses": 6000,
    "revision": "06892e828807ee5d482f7ba8eca04f74c7deb43b"
  },
  {
    "attempt": "initial",
    "event": "measured",
    "plan": "hobby",
    "requests": 6000,
    "parallel": 128,
    "repeat": 2,
    "region": "sjc",
    "seconds": 10.689,
    "http_statuses": {
      "200": 543,
      "429": 5457
    },
    "errors": {
      "tenant_busy": 5457
    },
    "accepted_per_second": 50.8,
    "hobby_max_in_window": 634,
    "exceeds_hobby": false,
    "configured_requests_per_second": 50,
    "configured_burst": 100,
    "plan_max_in_window": 635,
    "verified_local_responses": 6000,
    "revision": "06892e828807ee5d482f7ba8eca04f74c7deb43b"
  },
  {
    "attempt": "initial",
    "event": "measured",
    "plan": "hobby",
    "requests": 6000,
    "parallel": 128,
    "repeat": 3,
    "region": "sjc",
    "seconds": 10.392,
    "http_statuses": {
      "429": 5471,
      "200": 529
    },
    "errors": {
      "tenant_busy": 5471
    },
    "accepted_per_second": 50.9,
    "hobby_max_in_window": 619,
    "exceeds_hobby": false,
    "configured_requests_per_second": 50,
    "configured_burst": 100,
    "plan_max_in_window": 620,
    "verified_local_responses": 6000,
    "revision": "06892e828807ee5d482f7ba8eca04f74c7deb43b"
  },
  {
    "attempt": "initial",
    "event": "ready",
    "region": "fra",
    "revision": "06892e828807ee5d482f7ba8eca04f74c7deb43b",
    "sql_status": 200,
    "sql_region": "fra",
    "sql_machine": "ts-fra-1",
    "image": "ghcr.io/terrascale/terrabase@sha256:7cbd16a61af065243a28848fc86201006a2d28b570e9ebc1ab420c91bb82605e"
  },
  {
    "attempt": "initial",
    "event": "warmup",
    "plan": "hobby",
    "requests": 3000,
    "parallel": 128,
    "repeat": 0,
    "region": "fra",
    "seconds": 5.902,
    "http_statuses": {
      "200": 368,
      "429": 2632
    },
    "errors": {
      "tenant_busy": 2632
    },
    "accepted_per_second": 62.35,
    "hobby_max_in_window": 395,
    "exceeds_hobby": false,
    "configured_requests_per_second": 50,
    "configured_burst": 100,
    "plan_max_in_window": 396,
    "verified_local_responses": 3000,
    "revision": "06892e828807ee5d482f7ba8eca04f74c7deb43b"
  },
  {
    "attempt": "initial",
    "event": "measured",
    "plan": "hobby",
    "requests": 6000,
    "parallel": 128,
    "repeat": 1,
    "region": "fra",
    "seconds": 11.024,
    "http_statuses": {
      "200": 557,
      "429": 5443
    },
    "errors": {
      "tenant_busy": 5443
    },
    "accepted_per_second": 50.53,
    "hobby_max_in_window": 651,
    "exceeds_hobby": false,
    "configured_requests_per_second": 50,
    "configured_burst": 100,
    "plan_max_in_window": 652,
    "verified_local_responses": 6000,
    "revision": "06892e828807ee5d482f7ba8eca04f74c7deb43b"
  },
  {
    "attempt": "initial",
    "event": "measured",
    "plan": "hobby",
    "requests": 6000,
    "parallel": 128,
    "repeat": 2,
    "region": "fra",
    "seconds": 10.911,
    "http_statuses": {
      "200": 554,
      "429": 5446
    },
    "errors": {
      "tenant_busy": 5446
    },
    "accepted_per_second": 50.77,
    "hobby_max_in_window": 645,
    "exceeds_hobby": false,
    "configured_requests_per_second": 50,
    "configured_burst": 100,
    "plan_max_in_window": 646,
    "verified_local_responses": 6000,
    "revision": "06892e828807ee5d482f7ba8eca04f74c7deb43b"
  },
  {
    "attempt": "initial",
    "event": "measured",
    "plan": "hobby",
    "requests": 6000,
    "parallel": 128,
    "repeat": 3,
    "region": "fra",
    "seconds": 11.072,
    "http_statuses": {
      "200": 562,
      "429": 5438
    },
    "errors": {
      "tenant_busy": 5438
    },
    "accepted_per_second": 50.76,
    "hobby_max_in_window": 653,
    "exceeds_hobby": false,
    "configured_requests_per_second": 50,
    "configured_burst": 100,
    "plan_max_in_window": 654,
    "verified_local_responses": 6000,
    "revision": "06892e828807ee5d482f7ba8eca04f74c7deb43b"
  },
  {
    "attempt": "initial",
    "event": "published",
    "region": "gru",
    "organization": "01M6NP7QGXD11EGWMMYAHVGQ2G",
    "plan": "pro",
    "revision": "06892e828807ee5d482f7ba8eca04f74c7deb43b"
  },
  {
    "attempt": "initial",
    "event": "ready",
    "region": "gru",
    "revision": "06892e828807ee5d482f7ba8eca04f74c7deb43b",
    "sql_status": 200,
    "sql_region": "gru",
    "sql_machine": "ts-gru-1",
    "image": "ghcr.io/terrascale/terrabase@sha256:7cbd16a61af065243a28848fc86201006a2d28b570e9ebc1ab420c91bb82605e"
  },
  {
    "attempt": "initial",
    "event": "ready",
    "region": "sjc",
    "revision": "06892e828807ee5d482f7ba8eca04f74c7deb43b",
    "sql_status": 200,
    "sql_region": "sjc",
    "sql_machine": "ts-sjc-1",
    "image": "ghcr.io/terrascale/terrabase@sha256:7cbd16a61af065243a28848fc86201006a2d28b570e9ebc1ab420c91bb82605e"
  },
  {
    "attempt": "initial",
    "event": "warmup",
    "plan": "pro",
    "requests": 3000,
    "parallel": 128,
    "repeat": 0,
    "region": "sjc",
    "seconds": 5.634,
    "http_statuses": {
      "200": 2879,
      "429": 121
    },
    "errors": {
      "tenant_busy": 121
    },
    "accepted_per_second": 510.97,
    "hobby_max_in_window": 381,
    "exceeds_hobby": true,
    "configured_requests_per_second": 500,
    "configured_burst": 1000,
    "plan_max_in_window": 3818,
    "verified_local_responses": 3000,
    "revision": "06892e828807ee5d482f7ba8eca04f74c7deb43b"
  },
  {
    "attempt": "initial",
    "event": "measured",
    "plan": "pro",
    "requests": 6000,
    "parallel": 128,
    "repeat": 1,
    "region": "sjc",
    "seconds": 10.481,
    "http_statuses": {
      "200": 5749,
      "429": 251
    },
    "errors": {
      "tenant_busy": 251
    },
    "accepted_per_second": 548.51,
    "hobby_max_in_window": 624,
    "exceeds_hobby": true,
    "configured_requests_per_second": 500,
    "configured_burst": 1000,
    "plan_max_in_window": 6241,
    "verified_local_responses": 6000,
    "revision": "06892e828807ee5d482f7ba8eca04f74c7deb43b"
  },
  {
    "attempt": "initial",
    "event": "measured",
    "plan": "pro",
    "requests": 6000,
    "parallel": 128,
    "repeat": 2,
    "region": "sjc",
    "seconds": 10.403,
    "http_statuses": {
      "200": 5257,
      "429": 743
    },
    "errors": {
      "tenant_busy": 743
    },
    "accepted_per_second": 505.32,
    "hobby_max_in_window": 620,
    "exceeds_hobby": true,
    "configured_requests_per_second": 500,
    "configured_burst": 1000,
    "plan_max_in_window": 6202,
    "verified_local_responses": 6000,
    "revision": "06892e828807ee5d482f7ba8eca04f74c7deb43b"
  },
  {
    "attempt": "initial",
    "event": "measured",
    "plan": "pro",
    "requests": 6000,
    "parallel": 128,
    "repeat": 3,
    "region": "sjc",
    "seconds": 10.541,
    "http_statuses": {
      "200": 5327,
      "429": 673
    },
    "errors": {
      "tenant_busy": 673
    },
    "accepted_per_second": 505.36,
    "hobby_max_in_window": 627,
    "exceeds_hobby": true,
    "configured_requests_per_second": 500,
    "configured_burst": 1000,
    "plan_max_in_window": 6271,
    "verified_local_responses": 6000,
    "revision": "06892e828807ee5d482f7ba8eca04f74c7deb43b"
  },
  {
    "attempt": "initial",
    "event": "ready",
    "region": "fra",
    "revision": "06892e828807ee5d482f7ba8eca04f74c7deb43b",
    "sql_status": 200,
    "sql_region": "fra",
    "sql_machine": "ts-fra-1",
    "image": "ghcr.io/terrascale/terrabase@sha256:7cbd16a61af065243a28848fc86201006a2d28b570e9ebc1ab420c91bb82605e"
  },
  {
    "attempt": "initial",
    "event": "warmup",
    "plan": "pro",
    "requests": 3000,
    "parallel": 128,
    "repeat": 0,
    "region": "fra",
    "seconds": 6.131,
    "http_statuses": {
      "200": 2556,
      "429": 444
    },
    "errors": {
      "tenant_busy": 444
    },
    "accepted_per_second": 416.91,
    "hobby_max_in_window": 406,
    "exceeds_hobby": true,
    "configured_requests_per_second": 500,
    "configured_burst": 1000,
    "plan_max_in_window": 4066,
    "verified_local_responses": 3000,
    "revision": "06892e828807ee5d482f7ba8eca04f74c7deb43b"
  },
  {
    "attempt": "initial",
    "event": "measured",
    "plan": "pro",
    "requests": 6000,
    "parallel": 128,
    "repeat": 1,
    "region": "fra",
    "seconds": 11.149,
    "http_statuses": {
      "200": 5784,
      "429": 216
    },
    "errors": {
      "tenant_busy": 216
    },
    "accepted_per_second": 518.8,
    "hobby_max_in_window": 657,
    "exceeds_hobby": true,
    "configured_requests_per_second": 500,
    "configured_burst": 1000,
    "plan_max_in_window": 6575,
    "verified_local_responses": 6000,
    "revision": "06892e828807ee5d482f7ba8eca04f74c7deb43b"
  },
  {
    "attempt": "initial",
    "event": "measured",
    "plan": "pro",
    "requests": 6000,
    "parallel": 128,
    "repeat": 2,
    "region": "fra",
    "seconds": 11.355,
    "http_statuses": {
      "200": 5953,
      "429": 47
    },
    "errors": {
      "tenant_busy": 47
    },
    "accepted_per_second": 524.27,
    "hobby_max_in_window": 667,
    "exceeds_hobby": true,
    "configured_requests_per_second": 500,
    "configured_burst": 1000,
    "plan_max_in_window": 6678,
    "verified_local_responses": 6000,
    "revision": "06892e828807ee5d482f7ba8eca04f74c7deb43b"
  },
  {
    "attempt": "initial",
    "event": "measured",
    "plan": "pro",
    "requests": 6000,
    "parallel": 128,
    "repeat": 3,
    "region": "fra",
    "seconds": 11.096,
    "http_statuses": {
      "200": 1334,
      "429": 4666
    },
    "errors": {
      "tenant_busy": 4666
    },
    "accepted_per_second": 120.23,
    "hobby_max_in_window": 654,
    "exceeds_hobby": true,
    "configured_requests_per_second": 500,
    "configured_burst": 1000,
    "plan_max_in_window": 6548,
    "verified_local_responses": 6000,
    "revision": "06892e828807ee5d482f7ba8eca04f74c7deb43b"
  },
  {
    "attempt": "republish-per-window",
    "removed": 0,
    "kept_live": 2,
    "freed_bytes": 0,
    "free_percent": 57.84
  },
  {
    "attempt": "republish-per-window",
    "event": "ready",
    "region": "sjc",
    "revision": "d450cbe3b439b79f3b9710e1d7fe11e03e1a50d7",
    "sql_status": 200,
    "sql_region": "sjc",
    "sql_machine": "ts-sjc-1",
    "image": "ghcr.io/terrascale/terrabase@sha256:b4fab3f1dd8363d4be5d8bd19decd20b6057084fb096aa54073f1b6b4b84fd12"
  },
  {
    "attempt": "republish-per-window",
    "event": "republished_before_window",
    "region": "sjc",
    "repeat": 0,
    "plan": "pro"
  },
  {
    "attempt": "republish-per-window",
    "event": "warmup",
    "plan": "pro",
    "requests": 3000,
    "parallel": 128,
    "repeat": 0,
    "region": "sjc",
    "seconds": 5.719,
    "http_statuses": {
      "200": 2868,
      "429": 132
    },
    "errors": {
      "tenant_busy": 132
    },
    "accepted_per_second": 501.47,
    "hobby_max_in_window": 385,
    "exceeds_hobby": true,
    "configured_requests_per_second": 500,
    "configured_burst": 1000,
    "plan_max_in_window": 3860,
    "verified_local_responses": 3000,
    "revision": "d450cbe3b439b79f3b9710e1d7fe11e03e1a50d7"
  },
  {
    "attempt": "republish-per-window",
    "event": "republished_before_window",
    "region": "sjc",
    "repeat": 1,
    "plan": "pro"
  },
  {
    "attempt": "republish-per-window",
    "event": "measured",
    "plan": "pro",
    "requests": 6000,
    "parallel": 128,
    "repeat": 1,
    "region": "sjc",
    "seconds": 10.473,
    "http_statuses": {
      "200": 5756,
      "429": 244
    },
    "errors": {
      "tenant_busy": 244
    },
    "accepted_per_second": 549.59,
    "hobby_max_in_window": 623,
    "exceeds_hobby": true,
    "configured_requests_per_second": 500,
    "configured_burst": 1000,
    "plan_max_in_window": 6237,
    "verified_local_responses": 6000,
    "revision": "d450cbe3b439b79f3b9710e1d7fe11e03e1a50d7"
  },
  {
    "attempt": "republish-per-window",
    "event": "republished_before_window",
    "region": "sjc",
    "repeat": 2,
    "plan": "pro"
  },
  {
    "attempt": "republish-per-window",
    "event": "measured",
    "plan": "pro",
    "requests": 6000,
    "parallel": 128,
    "repeat": 2,
    "region": "sjc",
    "seconds": 10.401,
    "http_statuses": {
      "200": 5378,
      "429": 622
    },
    "errors": {
      "tenant_busy": 622
    },
    "accepted_per_second": 517.05,
    "hobby_max_in_window": 620,
    "exceeds_hobby": true,
    "configured_requests_per_second": 500,
    "configured_burst": 1000,
    "plan_max_in_window": 6201,
    "verified_local_responses": 6000,
    "revision": "d450cbe3b439b79f3b9710e1d7fe11e03e1a50d7"
  },
  {
    "attempt": "republish-per-window",
    "event": "republished_before_window",
    "region": "sjc",
    "repeat": 3,
    "plan": "pro"
  },
  {
    "attempt": "republish-per-window",
    "event": "measured",
    "plan": "pro",
    "requests": 6000,
    "parallel": 128,
    "repeat": 3,
    "region": "sjc",
    "seconds": 10.554,
    "http_statuses": {
      "200": 5482,
      "429": 518
    },
    "errors": {
      "tenant_busy": 518
    },
    "accepted_per_second": 519.44,
    "hobby_max_in_window": 627,
    "exceeds_hobby": true,
    "configured_requests_per_second": 500,
    "configured_burst": 1000,
    "plan_max_in_window": 6277,
    "verified_local_responses": 6000,
    "revision": "d450cbe3b439b79f3b9710e1d7fe11e03e1a50d7"
  },
  {
    "attempt": "republish-per-window",
    "event": "ready",
    "region": "fra",
    "revision": "d450cbe3b439b79f3b9710e1d7fe11e03e1a50d7",
    "sql_status": 200,
    "sql_region": "fra",
    "sql_machine": "ts-fra-1",
    "image": "ghcr.io/terrascale/terrabase@sha256:b4fab3f1dd8363d4be5d8bd19decd20b6057084fb096aa54073f1b6b4b84fd12"
  },
  {
    "attempt": "republish-per-window",
    "event": "republished_before_window",
    "region": "fra",
    "repeat": 0,
    "plan": "pro"
  },
  {
    "attempt": "republish-per-window",
    "event": "warmup",
    "plan": "pro",
    "requests": 3000,
    "parallel": 128,
    "repeat": 0,
    "region": "fra",
    "seconds": 6.051,
    "http_statuses": {
      "200": 2950,
      "429": 50
    },
    "errors": {
      "tenant_busy": 50
    },
    "accepted_per_second": 487.55,
    "hobby_max_in_window": 402,
    "exceeds_hobby": true,
    "configured_requests_per_second": 500,
    "configured_burst": 1000,
    "plan_max_in_window": 4026,
    "verified_local_responses": 3000,
    "revision": "d450cbe3b439b79f3b9710e1d7fe11e03e1a50d7"
  },
  {
    "attempt": "republish-per-window",
    "event": "republished_before_window",
    "region": "fra",
    "repeat": 1,
    "plan": "pro"
  },
  {
    "attempt": "republish-per-window",
    "event": "measured",
    "plan": "pro",
    "requests": 6000,
    "parallel": 128,
    "repeat": 1,
    "region": "fra",
    "seconds": 10.916,
    "http_statuses": {
      "200": 1256,
      "429": 4744
    },
    "errors": {
      "tenant_busy": 4744
    },
    "accepted_per_second": 115.06,
    "hobby_max_in_window": 645,
    "exceeds_hobby": true,
    "configured_requests_per_second": 500,
    "configured_burst": 1000,
    "plan_max_in_window": 6459,
    "verified_local_responses": 6000,
    "revision": "d450cbe3b439b79f3b9710e1d7fe11e03e1a50d7"
  },
  {
    "attempt": "fra-publication-failure",
    "removed": 1,
    "kept_live": 3,
    "freed_bytes": 0,
    "free_percent": 57.87
  },
  {
    "attempt": "fra-publication-failure",
    "event": "ready",
    "region": "fra",
    "revision": "d450cbe3b439b79f3b9710e1d7fe11e03e1a50d7",
    "sql_status": 200,
    "sql_region": "fra",
    "sql_machine": "ts-fra-1",
    "image": "ghcr.io/terrascale/terrabase@sha256:b4fab3f1dd8363d4be5d8bd19decd20b6057084fb096aa54073f1b6b4b84fd12"
  },
  {
    "attempt": "fra-publication-failure",
    "event": "republished_before_window",
    "region": "fra",
    "repeat": 0,
    "plan": "pro"
  },
  {
    "attempt": "fra-publication-failure",
    "event": "warmup",
    "plan": "pro",
    "requests": 3000,
    "parallel": 128,
    "repeat": 0,
    "region": "fra",
    "seconds": 6.279,
    "http_statuses": {
      "429": 197,
      "200": 2803
    },
    "errors": {
      "tenant_busy": 197
    },
    "accepted_per_second": 446.42,
    "hobby_max_in_window": 413,
    "exceeds_hobby": true,
    "configured_requests_per_second": 500,
    "configured_burst": 1000,
    "plan_max_in_window": 4140,
    "verified_local_responses": 3000,
    "revision": "d450cbe3b439b79f3b9710e1d7fe11e03e1a50d7",
    "qos_counter_delta": {
      "terrabase_qos_rejected_total{otel_scope_name=\"TerraScale.TerraBase\",plan=\"pro\",reason=\"concurrency\",tenant_bucket=\"22\"}": 197.0,
      "terrabase_qos_request_duration_seconds_count{otel_scope_name=\"TerraScale.TerraBase\",plan=\"pro\",tenant_bucket=\"22\"}": 2803.0
    }
  },
  {
    "attempt": "single-publication + QoS",
    "removed": 1,
    "kept_live": 3,
    "freed_bytes": 57389056,
    "free_percent": 57.06
  },
  {
    "attempt": "single-publication + QoS",
    "event": "ready",
    "region": "fra",
    "revision": "caf93a56825b8668bcc01ae476245052f461506d",
    "sql_status": 200,
    "sql_region": "fra",
    "sql_machine": "ts-fra-1",
    "image": "ghcr.io/terrascale/terrabase@sha256:ae31f3c437615de71ff3429a75df03b42e3d550c08076bb413bbbf783922d314"
  },
  {
    "attempt": "single-publication + QoS",
    "event": "republished_before_window",
    "region": "fra",
    "repeat": 0,
    "plan": "pro"
  },
  {
    "attempt": "single-publication + QoS",
    "event": "warmup",
    "plan": "pro",
    "requests": 3000,
    "parallel": 128,
    "repeat": 0,
    "region": "fra",
    "seconds": 6.205,
    "http_statuses": {
      "200": 2804,
      "429": 196
    },
    "errors": {
      "tenant_busy": 196
    },
    "accepted_per_second": 451.91,
    "hobby_max_in_window": 410,
    "exceeds_hobby": true,
    "configured_requests_per_second": 500,
    "configured_burst": 1000,
    "plan_max_in_window": 4103,
    "verified_local_responses": 3000,
    "revision": "caf93a56825b8668bcc01ae476245052f461506d",
    "qos_counter_delta": {
      "terrabase_qos_rejected_total{otel_scope_name=\"TerraScale.TerraBase\",plan=\"pro\",reason=\"concurrency\",tenant_bucket=\"22\"}": 196.0,
      "terrabase_qos_request_duration_seconds_count{otel_scope_name=\"TerraScale.TerraBase\",plan=\"pro\",tenant_bucket=\"22\"}": 2804.0
    }
  },
  {
    "attempt": "single-publication + QoS",
    "event": "measured",
    "plan": "pro",
    "requests": 6000,
    "parallel": 128,
    "repeat": 1,
    "region": "fra",
    "seconds": 11.111,
    "http_statuses": {
      "200": 5811,
      "429": 189
    },
    "errors": {
      "tenant_busy": 189
    },
    "accepted_per_second": 523.0,
    "hobby_max_in_window": 655,
    "exceeds_hobby": true,
    "configured_requests_per_second": 500,
    "configured_burst": 1000,
    "plan_max_in_window": 6556,
    "verified_local_responses": 6000,
    "revision": "caf93a56825b8668bcc01ae476245052f461506d",
    "qos_counter_delta": {
      "terrabase_qos_rejected_total{otel_scope_name=\"TerraScale.TerraBase\",plan=\"pro\",reason=\"concurrency\",tenant_bucket=\"22\"}": 189.0,
      "terrabase_qos_request_duration_seconds_count{otel_scope_name=\"TerraScale.TerraBase\",plan=\"pro\",tenant_bucket=\"22\"}": 5811.0
    }
  },
  {
    "attempt": "single-publication + QoS",
    "event": "measured",
    "plan": "pro",
    "requests": 6000,
    "parallel": 128,
    "repeat": 2,
    "region": "fra",
    "seconds": 11.296,
    "http_statuses": {
      "200": 4026,
      "429": 1974
    },
    "errors": {
      "tenant_busy": 1974
    },
    "accepted_per_second": 356.4,
    "hobby_max_in_window": 664,
    "exceeds_hobby": true,
    "configured_requests_per_second": 500,
    "configured_burst": 1000,
    "plan_max_in_window": 6649,
    "verified_local_responses": 6000,
    "revision": "caf93a56825b8668bcc01ae476245052f461506d",
    "qos_counter_delta": {
      "terrabase_qos_rejected_total{otel_scope_name=\"TerraScale.TerraBase\",plan=\"pro\",reason=\"concurrency\",tenant_bucket=\"22\"}": 91.0,
      "terrabase_qos_rejected_total{otel_scope_name=\"TerraScale.TerraBase\",plan=\"hobby\",reason=\"concurrency\",tenant_bucket=\"22\"}": 39.0,
      "terrabase_qos_rejected_total{otel_scope_name=\"TerraScale.TerraBase\",plan=\"hobby\",reason=\"rate\",tenant_bucket=\"22\"}": 1844.0,
      "terrabase_qos_request_duration_seconds_count{otel_scope_name=\"TerraScale.TerraBase\",plan=\"hobby\",tenant_bucket=\"22\"}": 284.0,
      "terrabase_qos_request_duration_seconds_count{otel_scope_name=\"TerraScale.TerraBase\",plan=\"pro\",tenant_bucket=\"22\"}": 3742.0
    }
  },
  {
    "attempt": "fresh-sjc",
    "event": "fresh_create",
    "status": 201,
    "ingress_region": "sjc",
    "response_region": "sjc",
    "seconds_after_ready": 0.884,
    "error": null,
    "organization": "01M6NZQ68Q1HS2JWNZAZSTB74Q",
    "project": "01M6NZQ6AW2FFR49K66HY17EM4",
    "database": "01M6NZQCZ3FKZNKV1SF35TCG2C",
    "dashboard_revision": "8082a063e2f3752151ecc6494c28839905a63f84",
    "terrabase_revision": "caf93a56825b8668bcc01ae476245052f461506d",
    "manual_plan_publications": 0,
    "retries": 0
  },
  {
    "attempt": "fresh-sjc",
    "event": "fresh_write",
    "status": 200,
    "database_home": "sjc",
    "ingress_region": "sjc",
    "response_region": "sjc",
    "seconds_after_ready": 1.504,
    "error": null,
    "rows_affected": 1,
    "write_route": null,
    "server_timing": "app;dur=209.39;desc=\"sjc\"",
    "terrabase_revision": "caf93a56825b8668bcc01ae476245052f461506d",
    "manual_plan_publications": 0,
    "retries": 0
  },
  {
    "attempt": "fresh-sjc",
    "event": "fresh_write",
    "status": 200,
    "database_home": "sjc",
    "ingress_region": "fra",
    "response_region": "sjc",
    "seconds_after_ready": 2.612,
    "error": null,
    "rows_affected": 1,
    "write_route": "route=home; reason=ddl; commit=home",
    "server_timing": "app;dur=682.59;desc=\"fra\"",
    "terrabase_revision": "caf93a56825b8668bcc01ae476245052f461506d",
    "manual_plan_publications": 0,
    "retries": 0
  },
  {
    "attempt": "fresh-sjc",
    "event": "fresh_database_cleanup",
    "status": 204,
    "database": "01M6NZQCZ3FKZNKV1SF35TCG2C"
  },
  {
    "attempt": "fresh-sjc",
    "event": "fresh_create",
    "status": 409,
    "ingress_region": "fra",
    "response_region": "fra",
    "seconds_after_ready": 3.74,
    "error": "database_limit",
    "organization": "01M6NZQ68Q1HS2JWNZAZSTB74Q",
    "project": "01M6NZQ6AW2FFR49K66HY17EM4",
    "database": null,
    "dashboard_revision": "8082a063e2f3752151ecc6494c28839905a63f84",
    "terrabase_revision": "caf93a56825b8668bcc01ae476245052f461506d",
    "manual_plan_publications": 0,
    "retries": 0
  },
  {
    "attempt": "fresh-sjc",
    "event": "fresh_account_cleanup",
    "tag": "regional-plan-new-1105",
    "exit_code": 1,
    "deleted": false
  },
  {
    "attempt": "fresh-sjc",
    "event": "fresh_account_cleanup_retry",
    "tag": "regional-plan-new-1105",
    "exit_code": 0,
    "deleted": true,
    "pending": false
  },
  {
    "attempt": "fresh-fra",
    "event": "fresh_create",
    "status": 201,
    "ingress_region": "fra",
    "response_region": "fra",
    "seconds_after_ready": 0.926,
    "error": null,
    "organization": "01M6NZTW648AMBNZ5Q6MCTW9AP",
    "project": "01M6NZTW7VB25RGEE5CTVD3JHP",
    "database": "01M6NZV2CJBEJC88ENKR677TSK",
    "dashboard_revision": "8082a063e2f3752151ecc6494c28839905a63f84",
    "terrabase_revision": "caf93a56825b8668bcc01ae476245052f461506d",
    "manual_plan_publications": 0,
    "retries": 0
  },
  {
    "attempt": "fresh-fra",
    "event": "fresh_write",
    "status": 200,
    "database_home": "fra",
    "ingress_region": "sjc",
    "response_region": "fra",
    "seconds_after_ready": 2.634,
    "error": null,
    "rows_affected": 1,
    "write_route": "route=home; reason=ddl; commit=home",
    "server_timing": "app;dur=1293.51;desc=\"sjc\"",
    "terrabase_revision": "caf93a56825b8668bcc01ae476245052f461506d",
    "manual_plan_publications": 0,
    "retries": 0
  },
  {
    "attempt": "fresh-fra",
    "event": "fresh_write",
    "status": 200,
    "database_home": "fra",
    "ingress_region": "fra",
    "response_region": "fra",
    "seconds_after_ready": 3.084,
    "error": null,
    "rows_affected": 1,
    "write_route": null,
    "server_timing": "app;dur=25.40;desc=\"fra\"",
    "terrabase_revision": "caf93a56825b8668bcc01ae476245052f461506d",
    "manual_plan_publications": 0,
    "retries": 0
  },
  {
    "attempt": "fresh-fra",
    "event": "fresh_database_cleanup",
    "status": 204,
    "database": "01M6NZV2CJBEJC88ENKR677TSK"
  },
  {
    "attempt": "fresh-fra",
    "event": "fresh_signup_proven",
    "organization": "01M6NZTW648AMBNZ5Q6MCTW9AP",
    "database_creates": 1,
    "regional_writes": 2,
    "plan_unknown": 0,
    "dashboard_revision": "8082a063e2f3752151ecc6494c28839905a63f84"
  },
  {
    "attempt": "fresh-fra",
    "event": "fresh_account_cleanup",
    "tag": "regional-plan-new-fra-1105",
    "exit_code": 1,
    "deleted": false
  },
  {
    "attempt": "fresh-fra",
    "event": "fresh_account_cleanup_retry",
    "tag": "regional-plan-new-fra-1105",
    "exit_code": 0,
    "deleted": true,
    "pending": false
  },
  {
    "attempt": "paid-cleanup",
    "event": "ready",
    "region": "gru",
    "revision": "caf93a56825b8668bcc01ae476245052f461506d",
    "sql_status": 200,
    "sql_region": "gru",
    "sql_machine": "ts-gru-1",
    "image": "ghcr.io/terrascale/terrabase@sha256:ae31f3c437615de71ff3429a75df03b42e3d550c08076bb413bbbf783922d314"
  },
  {
    "attempt": "paid-cleanup",
    "event": "paid_database_cleanup",
    "status": 204,
    "database": "01M6NPEF0BE1VN9A1CMFBQYXQA"
  },
  {
    "attempt": "paid-cleanup",
    "event": "paid_account_cleanup",
    "tag": "regional-plan-sol-1105",
    "exit_code": 1,
    "deleted": false
  },
  {
    "attempt": "paid-cleanup",
    "event": "paid_account_cleanup_retry",
    "tag": "regional-plan-sol-1105",
    "exit_code": 0,
    "deleted": true,
    "pending": false
  },
  {
    "attempt": "r13",
    "event": "r13_result",
    "repeat": 1,
    "sha": "06892e828807ee5d482f7ba8eca04f74c7deb43b",
    "seconds": 311.277,
    "exit_code": 0
  },
  {
    "attempt": "r13",
    "event": "r13_result",
    "repeat": 2,
    "sha": "06892e828807ee5d482f7ba8eca04f74c7deb43b",
    "seconds": 339.96,
    "exit_code": 0
  },
  {
    "attempt": "r13",
    "event": "r13_result",
    "repeat": 3,
    "sha": "06892e828807ee5d482f7ba8eca04f74c7deb43b",
    "seconds": 342.431,
    "exit_code": 0
  },
  {
    "attempt": "r13",
    "event": "r13_result",
    "repeat": 4,
    "sha": "06892e828807ee5d482f7ba8eca04f74c7deb43b",
    "seconds": 346.098,
    "exit_code": 0
  },
  {
    "attempt": "r13",
    "event": "r13_result",
    "repeat": 5,
    "sha": "06892e828807ee5d482f7ba8eca04f74c7deb43b",
    "seconds": 351.658,
    "exit_code": 0
  }
]
```

## Final rollout snapshot

```text
terrascale 8082a06 BUILD UNKNOWN
  BUILD CI/Fast timing budget SUCCESS 8082a06 https://github.com/TerraScale/terrascale/actions/runs/42038971263/job/127284675208
  BUILD CI/auth SUCCESS caf93a5 https://github.com/TerraScale/terrascale/actions/runs/42037438619/job/127279311609
  BUILD CI/auth-package SUCCESS caf93a5 https://github.com/TerraScale/terrascale/actions/runs/42037438619/job/127279409062
  BUILD CI/changes SUCCESS 8082a06 https://github.com/TerraScale/terrascale/actions/runs/42038971263/job/127284425192
  BUILD CI/libs SUCCESS caf93a5 https://github.com/TerraScale/terrascale/actions/runs/42037438619/job/127279311903
  BUILD CI/main SUCCESS caf93a5 https://github.com/TerraScale/terrascale/actions/runs/42037438619/job/127281550700
  BUILD CI/main-auth SUCCESS caf93a5 https://github.com/TerraScale/terrascale/actions/runs/42037438619/job/127283265493
  BUILD CI/native SUCCESS caf93a5 https://github.com/TerraScale/terrascale/actions/runs/42037438619/job/127279311535
  BUILD CI/terrabase SUCCESS caf93a5 https://github.com/TerraScale/terrascale/actions/runs/42037438619/job/127279311752
  BUILD CI/test SUCCESS 8082a06 https://github.com/TerraScale/terrascale/actions/runs/42038971263/job/127284618102
  BUILD Dashboard Fast/test SUCCESS 8082a06 https://github.com/TerraScale/terrascale/actions/runs/42038971140/job/127284423190
  BUILD Docs Fast/deploy / deploy SUCCESS caf93a5 https://github.com/TerraScale/terrascale/actions/runs/42037439095/job/127279272578
  BUILD Docs Fast/fast SUCCESS caf93a5 https://github.com/TerraScale/terrascale/actions/runs/42037439095/job/127279133047
  BUILD Images (GHCR)/Publish and release SUCCESS 8082a06 https://github.com/TerraScale/terrascale/actions/runs/42038971483/job/127285163598
  BUILD Images (GHCR)/ready SUCCESS 8082a06 https://github.com/TerraScale/terrascale/actions/runs/42039087999/job/127284825050
  BUILD Images (GHCR)/test / test SUCCESS 8082a06 https://github.com/TerraScale/terrascale/actions/runs/42038971483/job/127285071568
  BUILD SDK Go Release/tests SUCCESS 5f85405 https://github.com/TerraScale/terrascale/actions/runs/42022102910/job/127227130943
  BUILD SDK go Fast/fast / proof SUCCESS 5f85405 https://github.com/TerraScale/terrascale/actions/runs/42022102960/job/127227130881
  BUILD SDK js Fast/fast / proof SUCCESS 6cacd06 https://github.com/TerraScale/terrascale/actions/runs/42020852898/job/127222947203
  BUILD Landing/test UNKNOWN (no non-skipped result in recent main history)
  BUILD SDK dotnet Fast/fast UNKNOWN (no non-skipped result in recent main history)
  BUILD SDK python Fast/fast UNKNOWN (no non-skipped result in recent main history)
  BUILD SDK rust Fast/fast UNKNOWN (no non-skipped result in recent main history)
  PROBES FAIL
  PROBE CodeQL/Analyze (actions) SUCCESS 0337afb https://github.com/TerraScale/terrascale/actions/runs/41421246887/job/125197035399
  PROBE CodeQL/Analyze (cpp) SUCCESS 0337afb https://github.com/TerraScale/terrascale/actions/runs/41421246887/job/125197035202
  PROBE CodeQL/Analyze (csharp) SUCCESS 0337afb https://github.com/TerraScale/terrascale/actions/runs/41421246887/job/125197035558
  PROBE CodeQL/Analyze (go) SUCCESS 0337afb https://github.com/TerraScale/terrascale/actions/runs/41421246887/job/125197035311
  PROBE CodeQL/Analyze (javascript) SUCCESS 0337afb https://github.com/TerraScale/terrascale/actions/runs/41421246887/job/125197035544
  PROBE CodeQL/Analyze (python) SUCCESS 0337afb https://github.com/TerraScale/terrascale/actions/runs/41421246887/job/125197035527
  PROBE CodeQL/Analyze (rust) SUCCESS 0337afb https://github.com/TerraScale/terrascale/actions/runs/41421246887/job/125197035333
  PROBE Live signup email delivery/delivery SUCCESS ebd0285 https://github.com/TerraScale/terrascale/actions/runs/41906950481/job/126844051073
  PROBE Slow/changes SUCCESS 5f85405 https://github.com/TerraScale/terrascale/actions/runs/42024638315/job/127235667204
  PROBE Slow/native SUCCESS 5f85405 https://github.com/TerraScale/terrascale/actions/runs/42024638315/job/127235761840
  PROBE Slow/slow-auth-main FAIL 5f85405 https://github.com/TerraScale/terrascale/actions/runs/42024638315/job/127235898407
  PROBE Slow/slow-terrabase-main FAIL 5f85405 https://github.com/TerraScale/terrascale/actions/runs/42024638315/job/127235898574

```

## Probe sources

These are operator proof programs, not production settings. Create a new unique synthetic account on beta, keep the returned key-file path in a private `/tmp/regional-plan-account-path.txt`, and substitute its organization/project/database IDs. Run rate workloads through `ts-heavy-any` on a dev host, never on OCI. Always delete owned databases and the account afterward. The fixed tenant bucket in the instrumented historical driver is 22 for the recorded organization; compute the FNV-1a organization-ID hash masked with 63 for a new organization. The credential-printing remote snippet is captured privately by its parent process; it is never run as an uncaptured shell command.

### Initial baseline/rate driver

```python
import argparse, collections, json, math, os, pathlib, subprocess, tempfile, time
parser=argparse.ArgumentParser()
parser.add_argument("--project", required=True)
parser.add_argument("--organization", required=True)
parser.add_argument("--database", required=True)
parser.add_argument("--sha", required=True)
parser.add_argument("--publish", action="store_true")
parser.add_argument("--region", choices=["gru","sjc","fra"], required=True)
parser.add_argument("--measure", action="store_true")
parser.add_argument("--health-only", action="store_true")
parser.add_argument("--plan", choices=["hobby","pro"], default="pro")
args=parser.parse_args()
os.umask(0o077)
remote = r"""
import json,pathlib,subprocess
images={x.split()[0]:x.split()[1] for x in subprocess.check_output(["ctr","-n","terrascale","containers","ls"],text=True).splitlines()[1:]}
found=[]
for line in subprocess.check_output(["ctr","-n","terrascale","tasks","ls"],text=True).splitlines()[1:]:
 f=line.split()
 if len(f)>2 and f[2]=="RUNNING" and "terrascale/terrabase@" in images.get(f[0],""):
  pid=f[1]; env=dict(v.split("=",1) for v in pathlib.Path("/proc/"+pid+"/environ").read_bytes().decode().split("\0") if "=" in v)
  secret=env.get("TERRABASE_PEER_SECRET")
  if not secret:
   secret=(pathlib.Path("/proc/"+pid+"/root")/env["TERRABASE_PEER_SECRET_FILE"].lstrip("/")).read_text().strip()
  peers=(pathlib.Path("/proc/"+pid+"/root")/env["TS_PEERS_FILE"].lstrip("/")).read_text().splitlines()
  ip=next(line.split()[2] for line in peers if line.split()[1]==env["TS_REGION"])
  found.append({"credential":secret,"ip":ip,"region":env["TS_REGION"],"image":images[f[0]]})
if len(found)!=1: raise RuntimeError("expected one live TerraBase")
print(json.dumps(found[0]))
"""
node=json.loads(subprocess.run(["ssh","-o","BatchMode=yes","-o","ConnectTimeout=8","ts-"+args.region+"-1","sudo -n python3 -"],input=remote,text=True,capture_output=True,check=True,timeout=25).stdout)
with tempfile.TemporaryDirectory(prefix="regional-plan-probe-") as directory:
 root=pathlib.Path(directory)
 header=root/"headers"
 header.write_text("X-TerraBase-Peer-Credential: "+node["credential"]+"\nContent-Type: application/json\n")
 base="http://"+node["ip"]+":8081"
 client_header=root/"client-headers"
 def request(path, body=None, method=None):
  response_headers=root/"last-headers"
  selected=client_header if path.startswith("/v1/projects/") else header
  cmd=["curl","--silent","--show-error","--http2-prior-knowledge","--max-time","20","--header","@"+str(selected),"--dump-header",str(response_headers),"--write-out","\n%{http_code}",base+path]
  if body is not None: cmd += ["--data-binary",json.dumps(body)]
  if method: cmd += ["--request",method]
  result=subprocess.run(cmd,text=True,capture_output=True,check=True,timeout=25)
  payload,status=result.stdout.rsplit("\n",1)
  captured={}
  for line in response_headers.read_text().splitlines():
   if ":" in line:
    name,value=line.split(":",1); captured[name.lower()]=value.strip()
  return int(status),json.loads(payload),captured
 status,health,_=request("/healthz")
 assert status==200 and health["region"]==args.region
 if args.health_only:
  print(json.dumps({"event":"health","region":args.region,"revision":health.get("revision"),"image":node["image"]}),flush=True)
  raise SystemExit(0)
 assert health.get("revision")==args.sha, "live revision differs from shipped SHA"
 if args.publish:
  assert args.region=="gru"
  status,body,_=request("/admin/organizations/"+args.organization+"/plan",{"plan":args.plan},"PUT")
  assert status==200 and body["plan"]==args.plan, "regional plan publication failed"
  print(json.dumps({"event":"published","region":args.region,"organization":args.organization,"plan":args.plan,"revision":health["revision"]}),flush=True)
 key_source="import pathlib; p=pathlib.Path('/tmp/regional-plan-account-path.txt').read_text().strip(); print(pathlib.Path(p).read_text().strip())"
 if pathlib.Path("/tmp/regional-plan-account-path.txt").exists():
  key=pathlib.Path(pathlib.Path("/tmp/regional-plan-account-path.txt").read_text().strip()).read_text().strip()
 else:
  key=subprocess.run(["ssh","-o","BatchMode=yes","-o","ConnectTimeout=8","beta","python3 -"],input=key_source,text=True,capture_output=True,check=True,timeout=25).stdout.strip()
 import base64,urllib.parse
 auth_header=root/"token-headers"
 basic=base64.b64encode((urllib.parse.quote(key[:26],safe="")+":"+urllib.parse.quote(key,safe="")).encode()).decode()
 auth_header.write_text("Authorization: Basic "+basic+"\n")
 exchange=subprocess.run(["curl","--silent","--show-error","--fail","--max-time","25","--header","@"+str(auth_header),"--data-urlencode","grant_type=client_credentials","--data-urlencode","project="+args.project,"https://auth.terrascale.tech/terrascale/oauth/token"],text=True,capture_output=True,check=True,timeout=30)
 token=json.loads(exchange.stdout)["access_token"]
 client_header.write_text(header.read_text()+"X-TerraBase-Forwarded-For: 127.0.0.1\nAuthorization: Bearer "+token+"\nX-TerraScale-Region-Pin: "+args.region+"\n")
 path="/v1/projects/"+args.project+"/databases/"+args.database+"/sql/execute"
 body={"statements":[{"sql":"SELECT 1"}]}
 status,response,sql_headers=request(path,body)
 assert status==200, "regional SQL read failed with status "+str(status)
 assert sql_headers.get("x-terrabase-region")==args.region, "SQL response came from another region"
 assert "home;" not in sql_headers.get("server-timing",""), "SQL read forwarded home"
 print(json.dumps({"event":"ready","region":args.region,"revision":health["revision"],"sql_status":status,"sql_region":sql_headers.get("x-terrabase-region"),"sql_machine":sql_headers.get("x-terrabase-machine"),"image":node["image"]}),flush=True)
 if args.measure:
  for repeat in range(0,4):
   requests=3000 if repeat==0 else 6000
   config=root/("curl-"+str(repeat))
   lines=["parallel","parallel-max = 128"]
   for index in range(requests):
    if index: lines.append("next")
    lines += ["http2-prior-knowledge",'url = '+json.dumps(base+path),
              'header = '+json.dumps("@"+str(client_header)),
              'data-binary = '+json.dumps(json.dumps(body)),
              'output = '+json.dumps(str(root/("response-"+str(index)))),
              'dump-header = '+json.dumps(str(root/("response-headers-"+str(index)))),
              'max-time = 20','silent','show-error',
              'write-out = '+json.dumps(str(index)+" %{http_code}\\n")]
   config.write_text("\n".join(lines)+"\n")
   started=time.monotonic()
   result=subprocess.run(["curl","--config",str(config)],text=True,capture_output=True,timeout=180)
   elapsed=time.monotonic()-started
   assert result.returncode==0, "curl transfer failed"
   counts=collections.Counter()
   errors=collections.Counter()
   for row in result.stdout.splitlines():
    index,code=row.split(); counts[code]+=1
    received={}
    for line in (root/("response-headers-"+index)).read_text().splitlines():
     if ":" in line:
      name,value=line.split(":",1); received[name.lower()]=value.strip()
    assert received.get("x-terrabase-region")==args.region, "measurement response came from another region"
    assert "home;" not in received.get("server-timing",""), "measurement forwarded home"
    if code!="200":
     value=json.loads((root/("response-"+index)).read_text())
     errors[value.get("error",{}).get("code","unreported")]+=1
   hobby_ceiling=100+50*elapsed
   rate,burst=(500,1000) if args.plan=="pro" else (50,100)
   report={"event":"warmup" if repeat==0 else "measured","plan":args.plan,"requests":requests,"parallel":128,"repeat":repeat,"region":args.region,"seconds":round(elapsed,3),
           "http_statuses":dict(counts),"errors":dict(errors),
           "accepted_per_second":round(counts["200"]/elapsed,2),
           "hobby_max_in_window":math.floor(hobby_ceiling),"exceeds_hobby":counts["200"]>hobby_ceiling,
           "configured_requests_per_second":rate,"configured_burst":burst,
           "plan_max_in_window":math.ceil(burst+rate*elapsed),"verified_local_responses":sum(counts.values()),
           "revision":health["revision"]}
   print(json.dumps(report),flush=True)
   if args.plan=="pro":
    assert counts["200"]>hobby_ceiling, "network window did not prove admission above Hobby"
   assert counts["200"]<=math.ceil(burst+rate*elapsed), "observed admission exceeds published plan bucket"
   assert set(counts)<={"200","429"} and set(errors)<={"tenant_busy"}, "unexpected request failure"
```

### SJC republish-before-window driver

```python
import argparse, collections, json, math, os, pathlib, subprocess, tempfile, time
parser=argparse.ArgumentParser()
parser.add_argument("--project", required=True)
parser.add_argument("--organization", required=True)
parser.add_argument("--database", required=True)
parser.add_argument("--sha", required=True)
parser.add_argument("--publish", action="store_true")
parser.add_argument("--region", choices=["gru","sjc","fra"], required=True)
parser.add_argument("--measure", action="store_true")
parser.add_argument("--health-only", action="store_true")
parser.add_argument("--plan", choices=["hobby","pro"], default="pro")
args=parser.parse_args()
os.umask(0o077)
remote = r"""
import json,pathlib,subprocess
images={x.split()[0]:x.split()[1] for x in subprocess.check_output(["ctr","-n","terrascale","containers","ls"],text=True).splitlines()[1:]}
found=[]
for line in subprocess.check_output(["ctr","-n","terrascale","tasks","ls"],text=True).splitlines()[1:]:
 f=line.split()
 if len(f)>2 and f[2]=="RUNNING" and "terrascale/terrabase@" in images.get(f[0],""):
  pid=f[1]; env=dict(v.split("=",1) for v in pathlib.Path("/proc/"+pid+"/environ").read_bytes().decode().split("\0") if "=" in v)
  secret=env.get("TERRABASE_PEER_SECRET")
  if not secret:
   secret=(pathlib.Path("/proc/"+pid+"/root")/env["TERRABASE_PEER_SECRET_FILE"].lstrip("/")).read_text().strip()
  peers=(pathlib.Path("/proc/"+pid+"/root")/env["TS_PEERS_FILE"].lstrip("/")).read_text().splitlines()
  ip=next(line.split()[2] for line in peers if line.split()[1]==env["TS_REGION"])
  found.append({"credential":secret,"ip":ip,"region":env["TS_REGION"],"image":images[f[0]]})
if len(found)!=1: raise RuntimeError("expected one live TerraBase")
print(json.dumps(found[0]))
"""
node=json.loads(subprocess.run(["ssh","-o","BatchMode=yes","-o","ConnectTimeout=8","ts-"+args.region+"-1","sudo -n python3 -"],input=remote,text=True,capture_output=True,check=True,timeout=25).stdout)
with tempfile.TemporaryDirectory(prefix="regional-plan-probe-") as directory:
 root=pathlib.Path(directory)
 header=root/"headers"
 header.write_text("X-TerraBase-Peer-Credential: "+node["credential"]+"\nContent-Type: application/json\n")
 base="http://"+node["ip"]+":8081"
 client_header=root/"client-headers"
 def request(path, body=None, method=None):
  response_headers=root/"last-headers"
  selected=client_header if path.startswith("/v1/projects/") else header
  cmd=["curl","--silent","--show-error","--http2-prior-knowledge","--max-time","20","--header","@"+str(selected),"--dump-header",str(response_headers),"--write-out","\n%{http_code}",base+path]
  if body is not None: cmd += ["--data-binary",json.dumps(body)]
  if method: cmd += ["--request",method]
  result=subprocess.run(cmd,text=True,capture_output=True,check=True,timeout=25)
  payload,status=result.stdout.rsplit("\n",1)
  captured={}
  for line in response_headers.read_text().splitlines():
   if ":" in line:
    name,value=line.split(":",1); captured[name.lower()]=value.strip()
  return int(status),json.loads(payload),captured
 status,health,_=request("/healthz")
 assert status==200 and health["region"]==args.region
 if args.health_only:
  print(json.dumps({"event":"health","region":args.region,"revision":health.get("revision"),"image":node["image"]}),flush=True)
  raise SystemExit(0)
 assert subprocess.run(["git","merge-base","--is-ancestor",args.sha,health["revision"]],capture_output=True).returncode==0, "live revision lacks shipped change"
 if args.publish:
  assert args.region=="gru"
  status,body,_=request("/admin/organizations/"+args.organization+"/plan",{"plan":args.plan},"PUT")
  assert status==200 and body["plan"]==args.plan, "regional plan publication failed"
  print(json.dumps({"event":"published","region":args.region,"organization":args.organization,"plan":args.plan,"revision":health["revision"]}),flush=True)
 key_source="import pathlib; p=pathlib.Path('/tmp/regional-plan-account-path.txt').read_text().strip(); print(pathlib.Path(p).read_text().strip())"
 if pathlib.Path("/tmp/regional-plan-account-path.txt").exists():
  key=pathlib.Path(pathlib.Path("/tmp/regional-plan-account-path.txt").read_text().strip()).read_text().strip()
 else:
  key=subprocess.run(["ssh","-o","BatchMode=yes","-o","ConnectTimeout=8","beta","python3 -"],input=key_source,text=True,capture_output=True,check=True,timeout=25).stdout.strip()
 import base64,urllib.parse
 auth_header=root/"token-headers"
 basic=base64.b64encode((urllib.parse.quote(key[:26],safe="")+":"+urllib.parse.quote(key,safe="")).encode()).decode()
 auth_header.write_text("Authorization: Basic "+basic+"\n")
 exchange=subprocess.run(["curl","--silent","--show-error","--fail","--max-time","25","--header","@"+str(auth_header),"--data-urlencode","grant_type=client_credentials","--data-urlencode","project="+args.project,"https://auth.terrascale.tech/terrascale/oauth/token"],text=True,capture_output=True,check=True,timeout=30)
 token=json.loads(exchange.stdout)["access_token"]
 client_header.write_text(header.read_text()+"X-TerraBase-Forwarded-For: 127.0.0.1\nAuthorization: Bearer "+token+"\nX-TerraScale-Region-Pin: "+args.region+"\n")
 path="/v1/projects/"+args.project+"/databases/"+args.database+"/sql/execute"
 body={"statements":[{"sql":"SELECT 1"}]}
 status,response,sql_headers=request(path,body)
 assert status==200, "regional SQL read failed with status "+str(status)
 assert sql_headers.get("x-terrabase-region")==args.region, "SQL response came from another region"
 assert "home;" not in sql_headers.get("server-timing",""), "SQL read forwarded home"
 print(json.dumps({"event":"ready","region":args.region,"revision":health["revision"],"sql_status":status,"sql_region":sql_headers.get("x-terrabase-region"),"sql_machine":sql_headers.get("x-terrabase-machine"),"image":node["image"]}),flush=True)
 if args.measure:
  gru=json.loads(subprocess.run(["ssh","-o","BatchMode=yes","-o","ConnectTimeout=8","ts-gru-1","sudo -n python3 -"],input=remote,text=True,capture_output=True,check=True,timeout=25).stdout)
  plan_header=root/"plan-headers"
  plan_header.write_text("X-TerraBase-Peer-Credential: "+gru["credential"]+"\nContent-Type: application/json\n")
  for repeat in range(0,4):
   if args.plan=="pro":
    published=subprocess.run(["curl","--silent","--show-error","--fail","--http2-prior-knowledge","--max-time","20","--header","@"+str(plan_header),"--request","PUT","--data-binary",json.dumps({"plan":"pro"}),"http://"+gru["ip"]+":8081/admin/organizations/"+args.organization+"/plan"],text=True,capture_output=True,check=True,timeout=25)
    assert json.loads(published.stdout)["plan"]=="pro"
    print(json.dumps({"event":"republished_before_window","region":args.region,"repeat":repeat,"plan":"pro"}),flush=True)
   requests=3000 if repeat==0 else 6000
   config=root/("curl-"+str(repeat))
   lines=["parallel","parallel-max = 128"]
   for index in range(requests):
    if index: lines.append("next")
    lines += ["http2-prior-knowledge",'url = '+json.dumps(base+path),
              'header = '+json.dumps("@"+str(client_header)),
              'data-binary = '+json.dumps(json.dumps(body)),
              'output = '+json.dumps(str(root/("response-"+str(index)))),
              'dump-header = '+json.dumps(str(root/("response-headers-"+str(index)))),
              'max-time = 20','silent','show-error',
              'write-out = '+json.dumps(str(index)+" %{http_code}\\n")]
   config.write_text("\n".join(lines)+"\n")
   started=time.monotonic()
   result=subprocess.run(["curl","--config",str(config)],text=True,capture_output=True,timeout=180)
   elapsed=time.monotonic()-started
   assert result.returncode==0, "curl transfer failed"
   counts=collections.Counter()
   errors=collections.Counter()
   for row in result.stdout.splitlines():
    index,code=row.split(); counts[code]+=1
    received={}
    for line in (root/("response-headers-"+index)).read_text().splitlines():
     if ":" in line:
      name,value=line.split(":",1); received[name.lower()]=value.strip()
    assert received.get("x-terrabase-region")==args.region, "measurement response came from another region"
    assert "home;" not in received.get("server-timing",""), "measurement forwarded home"
    if code!="200":
     value=json.loads((root/("response-"+index)).read_text())
     errors[value.get("error",{}).get("code","unreported")]+=1
   hobby_ceiling=100+50*elapsed
   rate,burst=(500,1000) if args.plan=="pro" else (50,100)
   report={"event":"warmup" if repeat==0 else "measured","plan":args.plan,"requests":requests,"parallel":128,"repeat":repeat,"region":args.region,"seconds":round(elapsed,3),
           "http_statuses":dict(counts),"errors":dict(errors),
           "accepted_per_second":round(counts["200"]/elapsed,2),
           "hobby_max_in_window":math.floor(hobby_ceiling),"exceeds_hobby":counts["200"]>hobby_ceiling,
           "configured_requests_per_second":rate,"configured_burst":burst,
           "plan_max_in_window":math.ceil(burst+rate*elapsed),"verified_local_responses":sum(counts.values()),
           "revision":health["revision"]}
   print(json.dumps(report),flush=True)
   if args.plan=="pro":
    assert counts["200"]>hobby_ceiling, "network window did not prove admission above Hobby"
   if args.plan=="pro" and repeat>0:
    assert counts["200"]/elapsed>=400, "paid window did not sustain at least400 accepted/s"
   assert counts["200"]<=math.ceil(burst+rate*elapsed), "observed admission exceeds published plan bucket"
   assert set(counts)<={"200","429"} and set(errors)<={"tenant_busy"}, "unexpected request failure"

```

### Instrumented FRA driver (single publication)

```python
import argparse, collections, json, math, os, pathlib, subprocess, tempfile, time
parser=argparse.ArgumentParser()
parser.add_argument("--project", required=True)
parser.add_argument("--organization", required=True)
parser.add_argument("--database", required=True)
parser.add_argument("--sha", required=True)
parser.add_argument("--publish", action="store_true")
parser.add_argument("--region", choices=["gru","sjc","fra"], required=True)
parser.add_argument("--measure", action="store_true")
parser.add_argument("--health-only", action="store_true")
parser.add_argument("--plan", choices=["hobby","pro"], default="pro")
args=parser.parse_args()
os.umask(0o077)
remote = r"""
import json,pathlib,subprocess
images={x.split()[0]:x.split()[1] for x in subprocess.check_output(["ctr","-n","terrascale","containers","ls"],text=True).splitlines()[1:]}
found=[]
for line in subprocess.check_output(["ctr","-n","terrascale","tasks","ls"],text=True).splitlines()[1:]:
 f=line.split()
 if len(f)>2 and f[2]=="RUNNING" and "terrascale/terrabase@" in images.get(f[0],""):
  pid=f[1]; env=dict(v.split("=",1) for v in pathlib.Path("/proc/"+pid+"/environ").read_bytes().decode().split("\0") if "=" in v)
  secret=env.get("TERRABASE_PEER_SECRET")
  if not secret:
   secret=(pathlib.Path("/proc/"+pid+"/root")/env["TERRABASE_PEER_SECRET_FILE"].lstrip("/")).read_text().strip()
  peers=(pathlib.Path("/proc/"+pid+"/root")/env["TS_PEERS_FILE"].lstrip("/")).read_text().splitlines()
  ip=next(line.split()[2] for line in peers if line.split()[1]==env["TS_REGION"])
  found.append({"credential":secret,"ip":ip,"region":env["TS_REGION"],"image":images[f[0]]})
if len(found)!=1: raise RuntimeError("expected one live TerraBase")
print(json.dumps(found[0]))
"""
node=json.loads(subprocess.run(["ssh","-o","BatchMode=yes","-o","ConnectTimeout=8","ts-"+args.region+"-1","sudo -n python3 -"],input=remote,text=True,capture_output=True,check=True,timeout=25).stdout)
with tempfile.TemporaryDirectory(prefix="regional-plan-probe-") as directory:
 root=pathlib.Path(directory)
 header=root/"headers"
 header.write_text("X-TerraBase-Peer-Credential: "+node["credential"]+"\nContent-Type: application/json\n")
 base="http://"+node["ip"]+":8081"
 client_header=root/"client-headers"
 def request(path, body=None, method=None):
  response_headers=root/"last-headers"
  selected=client_header if path.startswith("/v1/projects/") else header
  cmd=["curl","--silent","--show-error","--http2-prior-knowledge","--max-time","20","--header","@"+str(selected),"--dump-header",str(response_headers),"--write-out","\n%{http_code}",base+path]
  if body is not None: cmd += ["--data-binary",json.dumps(body)]
  if method: cmd += ["--request",method]
  result=subprocess.run(cmd,text=True,capture_output=True,check=True,timeout=25)
  payload,status=result.stdout.rsplit("\n",1)
  captured={}
  for line in response_headers.read_text().splitlines():
   if ":" in line:
    name,value=line.split(":",1); captured[name.lower()]=value.strip()
  return int(status),json.loads(payload),captured
 status,health,_=request("/healthz")
 assert status==200 and health["region"]==args.region
 if args.health_only:
  print(json.dumps({"event":"health","region":args.region,"revision":health.get("revision"),"image":node["image"]}),flush=True)
  raise SystemExit(0)
 assert subprocess.run(["git","merge-base","--is-ancestor",args.sha,health["revision"]],capture_output=True).returncode==0, "live revision lacks shipped change"
 if args.publish:
  assert args.region=="gru"
  status,body,_=request("/admin/organizations/"+args.organization+"/plan",{"plan":args.plan},"PUT")
  assert status==200 and body["plan"]==args.plan, "regional plan publication failed"
  print(json.dumps({"event":"published","region":args.region,"organization":args.organization,"plan":args.plan,"revision":health["revision"]}),flush=True)
 key_source="import pathlib; p=pathlib.Path('/tmp/regional-plan-account-path.txt').read_text().strip(); print(pathlib.Path(p).read_text().strip())"
 if pathlib.Path("/tmp/regional-plan-account-path.txt").exists():
  key=pathlib.Path(pathlib.Path("/tmp/regional-plan-account-path.txt").read_text().strip()).read_text().strip()
 else:
  key=subprocess.run(["ssh","-o","BatchMode=yes","-o","ConnectTimeout=8","beta","python3 -"],input=key_source,text=True,capture_output=True,check=True,timeout=25).stdout.strip()
 import base64,urllib.parse
 auth_header=root/"token-headers"
 basic=base64.b64encode((urllib.parse.quote(key[:26],safe="")+":"+urllib.parse.quote(key,safe="")).encode()).decode()
 auth_header.write_text("Authorization: Basic "+basic+"\n")
 exchange=subprocess.run(["curl","--silent","--show-error","--fail","--max-time","25","--header","@"+str(auth_header),"--data-urlencode","grant_type=client_credentials","--data-urlencode","project="+args.project,"https://auth.terrascale.tech/terrascale/oauth/token"],text=True,capture_output=True,check=True,timeout=30)
 token=json.loads(exchange.stdout)["access_token"]
 client_header.write_text(header.read_text()+"X-TerraBase-Forwarded-For: 127.0.0.1\nAuthorization: Bearer "+token+"\nX-TerraScale-Region-Pin: "+args.region+"\n")
 path="/v1/projects/"+args.project+"/databases/"+args.database+"/sql/execute"
 body={"statements":[{"sql":"SELECT 1"}]}
 status,response,sql_headers=request(path,body)
 assert status==200, "regional SQL read failed with status "+str(status)
 assert sql_headers.get("x-terrabase-region")==args.region, "SQL response came from another region"
 assert "home;" not in sql_headers.get("server-timing",""), "SQL read forwarded home"
 print(json.dumps({"event":"ready","region":args.region,"revision":health["revision"],"sql_status":status,"sql_region":sql_headers.get("x-terrabase-region"),"sql_machine":sql_headers.get("x-terrabase-machine"),"image":node["image"]}),flush=True)
 if args.measure:
  gru=json.loads(subprocess.run(["ssh","-o","BatchMode=yes","-o","ConnectTimeout=8","ts-gru-1","sudo -n python3 -"],input=remote,text=True,capture_output=True,check=True,timeout=25).stdout)
  plan_header=root/"plan-headers"
  plan_header.write_text("X-TerraBase-Peer-Credential: "+gru["credential"]+"\nContent-Type: application/json\n")
  def snapshot():
   raw=subprocess.run(["ssh","-o","BatchMode=yes","-o","ConnectTimeout=8","ts-"+args.region+"-1","sudo -n python3 -"],input='import json,subprocess\nresult=subprocess.run(["curl","--silent","--show-error","--fail","--max-time","5","http://127.0.0.1:9091/metrics"],text=True,capture_output=True,check=True)\nvalues={}\nfor line in result.stdout.splitlines():\n if \'tenant_bucket="22"\' in line and (line.startswith("terrabase_qos_rejected_total{") or line.startswith("terrabase_qos_request_duration_seconds_count{")):\n  labels,value=line.rsplit(" ",1);values[labels]=float(value)\nprint(json.dumps(values))\n',text=True,capture_output=True,check=True,timeout=20)
   return json.loads(raw.stdout)
  for repeat in range(0,4):
   if args.plan=="pro" and repeat==0:
    published=subprocess.run(["curl","--silent","--show-error","--fail","--http2-prior-knowledge","--max-time","20","--header","@"+str(plan_header),"--request","PUT","--data-binary",json.dumps({"plan":"pro"}),"http://"+gru["ip"]+":8081/admin/organizations/"+args.organization+"/plan"],text=True,capture_output=True,check=True,timeout=25)
    assert json.loads(published.stdout)["plan"]=="pro"
    print(json.dumps({"event":"republished_before_window","region":args.region,"repeat":repeat,"plan":"pro"}),flush=True)
   requests=3000 if repeat==0 else 6000
   config=root/("curl-"+str(repeat))
   lines=["parallel","parallel-max = 128"]
   for index in range(requests):
    if index: lines.append("next")
    lines += ["http2-prior-knowledge",'url = '+json.dumps(base+path),
              'header = '+json.dumps("@"+str(client_header)),
              'data-binary = '+json.dumps(json.dumps(body)),
              'output = '+json.dumps(str(root/("response-"+str(index)))),
              'dump-header = '+json.dumps(str(root/("response-headers-"+str(index)))),
              'max-time = 20','silent','show-error',
              'write-out = '+json.dumps(str(index)+" %{http_code}\\n")]
   config.write_text("\n".join(lines)+"\n")
   before=snapshot()
   started=time.monotonic()
   result=subprocess.run(["curl","--config",str(config)],text=True,capture_output=True,timeout=180)
   elapsed=time.monotonic()-started
   after=snapshot()
   delta={key:after[key]-before.get(key,0) for key in after if after[key]!=before.get(key,0)}
   assert result.returncode==0, "curl transfer failed"
   counts=collections.Counter()
   errors=collections.Counter()
   for row in result.stdout.splitlines():
    index,code=row.split(); counts[code]+=1
    received={}
    for line in (root/("response-headers-"+index)).read_text().splitlines():
     if ":" in line:
      name,value=line.split(":",1); received[name.lower()]=value.strip()
    assert received.get("x-terrabase-region")==args.region, "measurement response came from another region"
    assert "home;" not in received.get("server-timing",""), "measurement forwarded home"
    if code!="200":
     value=json.loads((root/("response-"+index)).read_text())
     errors[value.get("error",{}).get("code","unreported")]+=1
   hobby_ceiling=100+50*elapsed
   rate,burst=(500,1000) if args.plan=="pro" else (50,100)
   report={"event":"warmup" if repeat==0 else "measured","plan":args.plan,"requests":requests,"parallel":128,"repeat":repeat,"region":args.region,"seconds":round(elapsed,3),
           "http_statuses":dict(counts),"errors":dict(errors),
           "accepted_per_second":round(counts["200"]/elapsed,2),
           "hobby_max_in_window":math.floor(hobby_ceiling),"exceeds_hobby":counts["200"]>hobby_ceiling,
           "configured_requests_per_second":rate,"configured_burst":burst,
           "plan_max_in_window":math.ceil(burst+rate*elapsed),"verified_local_responses":sum(counts.values()),
           "revision":health["revision"],"qos_counter_delta":delta}
   print(json.dumps(report),flush=True)
   if args.plan=="pro":
    assert counts["200"]>hobby_ceiling, "network window did not prove admission above Hobby"
   if args.plan=="pro" and repeat>0:
    assert not any(value>0 and 'plan="hobby"' in key for key,value in delta.items()), "billing reconciler changed plan during paid window"
    assert counts["200"]/elapsed>=400, "paid window did not sustain at least400 accepted/s"
   assert counts["200"]<=math.ceil(burst+rate*elapsed), "observed admission exceeds published plan bucket"
   assert set(counts)<={"200","429"} and set(errors)<={"tenant_busy"}, "unexpected request failure"

```

### Fresh signup driver

The first invocation used this source with the two sequential create attempts. The isolated FRA invocation changed only `for home in ["sjc","fra"]:` to `for home in ["fra"]:` and its final expected event counts to one create/two writes, and used a new tag. Both used `--dash-sha 8082a063e2f3752151ecc6494c28839905a63f84`.

```python
import argparse,base64,json,os,pathlib,subprocess,tempfile,time,urllib.parse,urllib.request
parser=argparse.ArgumentParser()
parser.add_argument("--tag",required=True)
parser.add_argument("--dash-sha",required=True)
args=parser.parse_args()
assert not (pathlib.Path.home()/".local/state/ts-throwaway-account"/(args.tag+".json")).exists(), "fresh proof requires a new account tag"
os.umask(0o077)
remote='\nimport json,pathlib,subprocess\nimages={x.split()[0]:x.split()[1] for x in subprocess.check_output(["ctr","-n","terrascale","containers","ls"],text=True).splitlines()[1:]}\nfound=[]\nfor line in subprocess.check_output(["ctr","-n","terrascale","tasks","ls"],text=True).splitlines()[1:]:\n f=line.split()\n if len(f)>2 and f[2]=="RUNNING" and "terrascale/terrabase@" in images.get(f[0],""):\n  pid=f[1]; env=dict(v.split("=",1) for v in pathlib.Path("/proc/"+pid+"/environ").read_bytes().decode().split("\\0") if "=" in v)\n  secret=env.get("TERRABASE_PEER_SECRET")\n  if not secret:\n   secret=(pathlib.Path("/proc/"+pid+"/root")/env["TERRABASE_PEER_SECRET_FILE"].lstrip("/")).read_text().strip()\n  peers=(pathlib.Path("/proc/"+pid+"/root")/env["TS_PEERS_FILE"].lstrip("/")).read_text().splitlines()\n  ip=next(line.split()[2] for line in peers if line.split()[1]==env["TS_REGION"])\n  found.append({"credential":secret,"ip":ip,"region":env["TS_REGION"],"image":images[f[0]]})\nif len(found)!=1: raise RuntimeError("expected one live TerraBase")\nprint(json.dumps(found[0]))\n'
subprocess.run(["git","fetch","origin"],capture_output=True,check=True)
with urllib.request.urlopen("https://dash.terrascale.tech/health/live",timeout=20) as response:
 dashboard=json.load(response)
assert subprocess.run(["git","merge-base","--is-ancestor",args.dash_sha,dashboard["version"]],capture_output=True).returncode==0,"dashboard has not deployed signup publication"
nodes={}
for region in ["gru","sjc","fra"]:
 value=subprocess.run(["ssh","-o","BatchMode=yes","-o","ConnectTimeout=8","ts-"+region+"-1","sudo -n python3 -"],input=remote,text=True,capture_output=True,check=True,timeout=25)
 nodes[region]=json.loads(value.stdout)
with tempfile.TemporaryDirectory(prefix="regional-signup-proof-") as directory:
 root=pathlib.Path(directory)
 client=False
 def request(region,path,body=None,method=None):
  header=root/(region+"-headers")
  lines="X-TerraBase-Peer-Credential: "+nodes[region]["credential"]+"\nContent-Type: application/json\n"
  if client:
   lines+="X-TerraBase-Forwarded-For: 127.0.0.1\nAuthorization: Bearer "+token+"\nX-TerraScale-Region-Pin: "+region+"\n"
  header.write_text(lines)
  response_headers=root/(region+"-response-headers")
  command=["curl","--silent","--show-error","--http2-prior-knowledge","--max-time","25","--header","@"+str(header),"--dump-header",str(response_headers),"--write-out","\n%{http_code}","http://"+nodes[region]["ip"]+":8081"+path]
  if body is not None: command.extend(["--data-binary",json.dumps(body)])
  if method: command.extend(["--request",method])
  result=subprocess.run(command,text=True,capture_output=True,check=True,timeout=30)
  payload,status=result.stdout.rsplit("\n",1)
  received={}
  for line in response_headers.read_text().splitlines():
   if ":" in line:
    name,value=line.split(":",1);received[name.lower()]=value.strip()
  return int(status),json.loads(payload) if payload else {},received
 for region in nodes:
  status,health,_=request(region,"/healthz")
  assert status==200 and health["region"]==region
  assert subprocess.run(["git","merge-base","--is-ancestor","06892e828807ee5d482f7ba8eca04f74c7deb43b",health["revision"]],capture_output=True).returncode==0,"TerraBase lacks regional plan fix"
  nodes[region]["revision"]=health["revision"]
 database=None;created=False;token=None;project=None
 try:
  created=True
  account=subprocess.run(["ts-throwaway-account",args.tag],capture_output=True,text=True,check=True,timeout=240)
  created=True
  ready=time.monotonic()
  key=pathlib.Path(account.stdout.strip().splitlines()[-1]).read_text().strip()
  state=json.loads((pathlib.Path.home()/".local/state/ts-throwaway-account"/(args.tag+".json")).read_text())
  project=state["projectId"];organization=state["organizationId"]
  auth=root/"token-headers"
  basic=base64.b64encode((urllib.parse.quote(key[:26],safe="")+":"+urllib.parse.quote(key,safe="")).encode()).decode()
  auth.write_text("Authorization: Basic "+basic+"\n")
  exchange=subprocess.run(["curl","--silent","--show-error","--fail","--max-time","20","--header","@"+str(auth),"--data-urlencode","grant_type=client_credentials","--data-urlencode","project="+project,"https://auth.terrascale.tech/terrascale/oauth/token"],text=True,capture_output=True,check=True,timeout=25)
  token=json.loads(exchange.stdout)["access_token"];client=True
  for home in ["sjc","fra"]:
   status,value,headers=request(home,"/v1/projects/"+project+"/databases",{"name":"signup-plan-proof-"+home,"homeRegion":home,"allRegions":True})
   print(json.dumps({"event":"fresh_create","status":status,"ingress_region":home,"response_region":headers.get("x-terrabase-region"),"seconds_after_ready":round(time.monotonic()-ready,3),"error":value.get("error",{}).get("code"),"organization":organization,"project":project,"database":value.get("id"),"dashboard_revision":dashboard["version"],"terrabase_revision":nodes[home]["revision"],"manual_plan_publications":0,"retries":0}),flush=True)
   assert status==201,"first database creation in "+home+" failed"
   database=value["id"];database_home=home
   for region in ["sjc","fra"]:
    body={"statements":[{"sql":"CREATE TABLE IF NOT EXISTS signup_plan_probe (id TEXT PRIMARY KEY)"},{"sql":"INSERT INTO signup_plan_probe (id) VALUES (?)","args":[region]}]}
    status,value,headers=request(region,"/v1/projects/"+project+"/databases/"+database+"/sql/execute",body)
    print(json.dumps({"event":"fresh_write","status":status,"database_home":home,"ingress_region":region,"response_region":headers.get("x-terrabase-region"),"seconds_after_ready":round(time.monotonic()-ready,3),"error":value.get("error",{}).get("code"),"rows_affected":value.get("results",[{}])[-1].get("rows_affected"),"write_route":headers.get("terrascale-write-route"),"server_timing":headers.get("server-timing"),"terrabase_revision":nodes[region]["revision"],"manual_plan_publications":0,"retries":0}),flush=True)
    assert status==200 and value["results"][-1]["rows_affected"]==1,"first regional write failed"
   status,_,_=request(home,"/v1/projects/"+project+"/databases/"+database,method="DELETE")
   print(json.dumps({"event":"fresh_database_cleanup","status":status,"database":database}),flush=True)
   assert status in [200,202,204,404],"proof database cleanup failed"
   database=None
  print(json.dumps({"event":"fresh_signup_proven","organization":organization,"database_creates":2,"regional_writes":4,"plan_unknown":0,"dashboard_revision":dashboard["version"]}),flush=True)
 finally:
  try:
   if database is not None and token is not None:
    status,_,_=request(database_home,"/v1/projects/"+project+"/databases/"+database,method="DELETE")
    print(json.dumps({"event":"fresh_database_cleanup","status":status,"database":database}),flush=True)
    assert status in [200,202,204,404],"proof database cleanup failed"
  finally:
   if created and (pathlib.Path.home()/".local/state/ts-throwaway-account"/(args.tag+".json")).exists():
    cleanup=subprocess.run(["ts-throwaway-account","--delete",args.tag],text=True,capture_output=True,timeout=180)
    print(json.dumps({"event":"fresh_account_cleanup","tag":args.tag,"exit_code":cleanup.returncode,"deleted":cleanup.stdout.strip()=="DELETED "+args.tag}),flush=True)
    assert cleanup.returncode==0 and cleanup.stdout.strip()=="DELETED "+args.tag,"proof account cleanup failed"

```

### R13 five-invocation runner

```python
import json,subprocess,time
subprocess.run(["make","terrabase-build"],check=True)
sha=subprocess.check_output(["git","rev-parse","HEAD"],text=True).strip()
for repeat in range(1,6):
 print(json.dumps({"event":"r13_start","repeat":repeat,"sha":sha}),flush=True)
 started=time.monotonic()
 result=subprocess.run(["dotnet","run","--project","tests/TerraBase.Slow","--configuration","Debug","--no-build","--","--treenode-filter","/*/*/RouterDifferentialTests/*","--minimum-expected-tests","1","--output","detailed","--no-ansi"])
 print(json.dumps({"event":"r13_result","repeat":repeat,"sha":sha,"seconds":round(time.monotonic()-started,3),"exit_code":result.returncode}),flush=True)
 if result.returncode: raise SystemExit(result.returncode)

```
