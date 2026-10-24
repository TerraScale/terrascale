# Production alert delivery — 2026-10-23

Measured on the existing OCI GRU host using the deployed release containing ts-orchestrator PR #134 (merged main `12fb0670b7fedb4011f2f4af1eb9622b2301b2f9`). No new capacity or IAM policy was added.

| Check | Measured result |
| --- | --- |
| ARM64 Prometheus 3.15.0 / Alertmanager 0.34.1 | Official pinned checksums and configuration/rule fixtures passed during installation |
| Monitoring services | Prometheus, Alertmanager and alert relay active; health endpoints ready |
| Unchanged installer rerun | Passed; all three main process IDs unchanged |
| Direct relay test | HTTP 202: both Cloudflare email and private ntfy providers accepted |
| Direct test inbox observation | 9.319 seconds |
| Alertmanager initial group inbox observation | 38.107 seconds |
| New alert in an existing group inbox observation | 27.410 seconds |
| Resolution inbox observation | 33.112 seconds |
| Readiness targets | Separate TerraBase, auth and dashboard series; failures and subsequent recovery observed |

The delivery measurement ran at approximately 15:23–15:26 UTC. Each email matched a unique synthetic alert name; existing-group and resolution messages contained both grouped alerts. The observations include SSH, provider latency and five-second IMAP polling, so they are upper bounds for these particular send-to-inbox cases. All synthetic alerts were explicitly resolved during cleanup. No user data was modified.

The private ntfy destination is stored in OCI Vault and was not logged or published. Provider acceptance does not prove receipt on a phone. The owner must subscribe using the documented terminal-only command and observe its one test; that remains the TS-820 validation gate.

This is single-host monitoring. Host loss also stops alert delivery. Native normalized request-SLO counters and auth/dashboard native metrics discovery remain absent; readiness and scrape health do not establish request availability or fleet coverage. The synthetic timings do not measure an actual lag-threshold breach or guarantee delivery through provider outages.
