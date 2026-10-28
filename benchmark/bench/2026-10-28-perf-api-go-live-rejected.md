# Rejected Go regional SDK parity control, 2026-10-28

This run failed its empty-cell gate. It proves neither SDK parity nor a speedup.
Only two raw HTTP cells were retained; no SDK cell or three-repeat comparison completed.

## Source and method

Beta, Go 1.27.3, harness `585fc78e150ef19ee2acd1439ba64f2028090703`;
public Go SDK `07ffb51e1d66fb2f7a42673683e731bdfc26d158`
(`v0.0.0-20261028013942-07ffb51e1d66`). Client binary SHA-256:
`5e8216443c9f8f78c45d1dcebe69264699592176f19c35b7c8ff9ee0b3462865`.
Focused client/driver tests and the build passed through `ts-heavy-any` before live use.

The intended matrix was three alternating repeats, public-discovered gru/sjc/fra,
256 B/4 KiB documents, c=1/16, and raw-h1/raw-h2/shipped SDK. Each cell used a
long-lived pool, bounded readiness, two-second warmup and five-second measurement.
Only operations starting and finishing inside that measurement window are counted.
Every response must have exact status, one correct region header, and the expected
document id/version/state/body. Raw controls also require their selected protocol.
Transport attempts and operation failures are counted separately, including warmup;
readiness failures and expected final-window cancellation are excluded from rejection.
There is no retry-policy override, TLS bypass, auth bypass or quota bypass.

The owned synthetic account used normal hosted signup, mail verification, device
consent and purpose-bound API-key approval. Its synthetic organization received the
Enterprise plan through authenticated `ts-orch netbird ssh` administration. The
peer credential remained on-host. No serving customer data was used.

## Retained measured cells

| Region | Bytes | c | Lane | Successful ops | Errors | Bad attempts | Operation failures | p50 ms | p99 ms |
| --- | ---: | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| fra | 256 | 1 | raw-h1 | 20 | 0 | 0 | 0 | 205.695 | 573.439 |
| fra | 256 | 1 | raw-h2 | 0 | 0 | 0 | 0 | unmeasured | unmeasured |

The raw-h1 window started at 03:15:41.472 UTC; raw-h2 at 03:15:52.460 UTC.
Raw-h2 observed four HTTP/2 responses across readiness/warmup/window boundaries,
but no operation both started and finished in its five-second recorded window.
Its numerical zero histogram is missing evidence, not zero latency. The process
stopped after retaining the row and accepted database deletion. The cause of the
long requests is not established. Even raw-h1 has only 20 samples; its p99 is not
qualified tail evidence. Do not infer that HTTP/2 is generally slower from this run.

The remaining heavy-admission slots were reserved without stopping other work.
Across setup, measurement and the first cleanup attempt, 255 one-second host samples
had external CPU mean/p95/max 3.01/6.93/16.83% of one CPU; no external compiler/test
was observed at >=10%. This host control does not repair the missing comparison.

## Deployment and cleanup limits

Authenticated internal probes at 03:16:03–03:16:11 UTC returned `ok`, service 0.1.0,
and storage format `18-e15d70682919`, but different deployed revisions:

| Region | Serving node | Revision |
| --- | --- | --- |
| gru | ts-gru-2 | `64fdad763ce4d777e665dd606cdf7e9672d0deca` |
| sjc | ts-sjc-1 | `8f398b3291551da11fe3dc0da2de17ead5e165b5` |
| fra | ts-fra-1 | `677290f91adb62e391a20d54c7f2bc049cdb2580` |

These observations followed the measured cells; they are not a before/after rollout
proof. The normal deployment was not paused or altered.

Database deletion was accepted. The account-deletion receipt stayed HTTP 202 through
two bounded 180-second cleanup attempts, after which retries stopped and private
recovery material and the synthetic inbox were retained. A later authoritative
receipt read returned HTTP 204. Only after that changed state, one resumed cleanup
completed successfully with `Synthetic account and inbox cleanup settled.` The
private recovery directory `/tmp/api-overhead-_wa9ox67` was then confirmed absent
on beta. No synthetic account, inbox or private recovery material remains.
The earlier helper did not retain the deletion-authorization response header for
explicit continuation. Background closure nevertheless completed normally; neither
the timeout's cause nor an authorization repair is established by this cleanup.

## Follow-up setup rejection (run b)

The next beta attempt used harness `1ab865ddc1402075a790faf308ffd31e625180d1`
and cleanup helper `b9db654d7ed0623568c800c3a83eff294508fd6f`. Its binary SHA-256
was `a90a02af1a742f5431cd84b7bfbec422ad0e53219fc137456bde6005faf3470b`.
Database creation failed before any latency cell. The failure's HTTP classification
was not retained, so its cause is unknown. Cleanup reconciliation was accepted;
that does not establish whether creation committed. No SDK/raw comparison exists.

Across setup and initial cleanup, 239 host samples had external CPU mean/p95/max
3.26/7.92/42.59% of one CPU, with no external compiler/test observed at >=10%.
The three health probes before setup still reported the differing revisions above.
Artifacts remain in `go-parity-live-20261028-b/` beside run a; analysis copies are
`/tmp/perf-api-go-live-b`. The executed script is retained as `run.sh` there.

Initial account cleanup timed out. One resumed cleanup hit a transport failure.
The authoritative receipt then returned 202 and the private recovery file retained
continuation authorization. A subsequent authorized continuation returned 202;
its bounded status polling also timed out. Retries stopped, and the private recovery
material and synthetic inbox remain retained. Account/inbox cleanup is incomplete.
A later receipt-only read returned HTTP 503. No continuation or new tenant was
started; the private recovery material remains available for reconciliation.

## Artifacts and reproduction

Beta retains `benchmark/bench/go-parity-live-20261028-a/` beneath the managed
`fix-api-go-parity-30f1346c96979846f2b60dd2b44190e683e10e0cd3bcca07a2ab959d3dd19af9`
checkout. It holds every raw row, run log, host/CPU samples, source identity, binary
hash and the executed runner. Temporary analysis copies are `/tmp/perf-api-go-live-a`.
The exact runner is archived at `benchmark/scripts/go-parity-live-20261028.sh`;
its fixed output path refuses an existing directory. It stages the tenant helper
from immutable commit `3b00282251210b85acd998cbe74c7ef6927f18f0` and uses the retained
CPU observer from the earlier HTTPS control. Run via `TS_HEAVY_HOST=beta ts-heavy-any
nice -n 19 bash benchmark/scripts/go-parity-live-20261028.sh --repo terrascale
--branch fix/api-go-parity` after staging this archived script on that source.
The original synthetic account is reconciled; any new run must use a fresh tenant
and output directory, and must diagnose the empty HTTP/2 window before comparison.
