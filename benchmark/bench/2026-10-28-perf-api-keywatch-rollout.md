# API key-watch rollout observation, 2026-10-28

Live after-measurements remain blocked; this is operational evidence, not proof of
production regression, successful rollout or request-latency improvement.

Main release [40640721515](https://github.com/TerraScale/terrascale/actions/runs/40640721515/job/122658097874)
for merged key-watch commit `dc414b8a281aeb8d03a53f310e1a2a138350a6c7`
failed in “Release to every region”. The job log's bake reason was
`bake TerraBase log coverage lost`; its candidate check was `ok`. Recovery checks
reported TerraBase logs `ok`, ts-auth HTTP 503 and ts-dashboard HTTP 503.
The workflow dispatched a fresh normal main run; no manual rollout, workflow
change or serving-data reset was performed. This establishes the release-gate
failure, not its underlying cause. Auth gru readiness was already 503 beforehand.

Authenticated serving-health probes through ts-orch NetBird SSH, 06:21–06:22 UTC:

| Region | Serving revision | Health | Storage format |
| --- | --- | --- | --- |
| gru | `64fdad763ce4d777e665dd606cdf7e9672d0deca` | ok | `18-e15d70682919` |
| sjc | `8f398b3291551da11fe3dc0da2de17ead5e165b5` | ok | `18-e15d70682919` |
| fra | `dc414b8a281aeb8d03a53f310e1a2a138350a6c7` | ok | `18-e15d70682919` |

The gru auth container still returned healthz 200 and readyz 503/not_ready with
its unchanged image. No matching auth container was found in the checked
namespace on the sjc/fra host aliases; this is not proof of their public auth
availability. No new synthetic tenant was created, and the retained tenant was
not retried while authoritative readiness remained unchanged.

Revisions are serving-process health responses, not inferred from a green build
or image tag. Local evidence: `/tmp/perf-api-795-release-job.log`,
`/tmp/perf-api-prod-revisions-20261028-e.json`, and
`/tmp/perf-api-auth-region-state-20261028-e.json`. Job logs were read with `gh api`
after `gh run view` could not expose a completed job inside an ongoing workflow.
Only allowlisted health fields were exported; credentials remained on the hosts.
