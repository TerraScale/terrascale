# AI Agent Rules

> [!IMPORTANT]
> **AI agents MUST NOT edit this file. Only humans may modify it.**

## Project

TerraBase is a **leaderless, multi-region document database** focused on fast and efficient synchronization and replication.

When interacting with Forgejo, always use the `fj` CLI.

# Rules

## Language

- This repository MUST use **Rust** primarily.

## General
- Never add AI attribution, generated-by notices, or co-author trailers to commits, PR titles, or PR descriptions.
- Prefer simple, maintainable code and established libraries over custom solutions or unnecessary abstractions.
- Follow repository conventions, keep changes focused, and avoid unrelated rewrites.
- Never invent APIs, package names, commands, issue IDs, or successful verification results.

## Dependencies

- Prefer mature, popular, actively maintained, and well-tested Cargo crates over custom implementations.
- Do not build functionality from scratch when a suitable established crate already exists unless there is a clear technical reason.

## Metrics

- TerraBase MUST expose **Prometheus-compatible metrics** for relevant operational and performance data.

## Build and Resource Usage

- Rust compilation and Makefile tasks MUST run at the **lowest practical CPU scheduling priority**.
- Build processes SHOULD yield CPU resources to other workloads whenever possible.
- Always use the repository `Makefile` when an equivalent Make target exists.

## Code

- Keep code clean, modular, consistent, and easy to navigate.
- Keep responsibilities clearly separated.
- Avoid unnecessary abstraction, duplication, and complexity.
- Follow existing repository structure and conventions unless there is a strong reason to change them.

## Networking

- TerraBase-to-TerraBase communication MUST use **gRPC**.
- Inter-node gRPC communication MUST use **LZ4 compression** to reduce bandwidth usage.
- Do not introduce alternative inter-node protocols without explicit human approval.

## Benchmarks

- ALL benchmarks MUST live in the sibling repository:

  `../ts-benchmark`

- Do NOT add benchmark code, benchmark datasets, benchmark reports, or benchmark results to this repository.
- Performance comparisons and historical benchmark results MUST remain in `../ts-benchmark`.

## Testing

Maintain exactly two integration test suites:

- **Fast**
  - Covers critical paths and common user workflows.
  - MUST remain fast enough to run routinely.
  - MUST run before every merge, deployment, release, and relevant CI action.
  - Any failure MUST block the operation.

- **Slow**
  - Provides comprehensive integration and end-to-end coverage.
  - MUST run independently from the release-critical pipeline.
  - MUST NOT block merges, deployments, or releases.
  - Any failure MUST automatically create a Forgejo issue containing enough information to reproduce and investigate the failure.

### Testing Constraints

- Do NOT create unit tests.
- All tests MUST validate behavior through integration or end-to-end testing.
- New functionality MUST be covered by the appropriate integration test suite.
- Critical or commonly used functionality SHOULD be covered by the **Fast** suite.
- Broader, expensive, edge-case, stress, or exhaustive scenarios SHOULD be covered by the **Slow** suite.