# Cleanup and webhook verification, 2026-09-05

Source baseline: `56d45c2`, with the working-tree changes described below.

## Cleanup review

The five tracked root files `deadprobe`, `deadprobe2`, `deadprobe3`,
`deadprobe4`, and `deadprobe5` were Linux ELF executables containing debug
information. Searches of source, scripts, manifests, documentation, and build
inputs found no references to those names. Cargo's explicit binary targets are
source files; neither the Makefile nor the stack scripts consume these probes.
They were deleted as generated leftovers, removing about 21 MB of tracked
binaries. The ignored `rust_out` and `target/` local outputs were left alone.

The external backup adapter remains active: server `backup.rs` launches its
configured Litestream executable for streaming and restoration. It cannot be
removed until the native replacement meets ADR 0046's restore-chain,
encryption, residency, multi-database resource, and upstream comparison gates.
This is an unfinished retirement dependency, not a passed G1 gate.

## Webhook destination security

The HTTPS adapter already resolves each delivery attempt and pins the resulting
addresses into its transport. Its IPv6 policy previously admitted translation,
tunnel, and deprecated site-local destinations, including NAT64 addresses that
encode private IPv4 hosts. Admission now requires native global-unicast IPv6
outside the special-purpose and documentation blocks; mapped IPv4 still passes
through the IPv4 policy. This applies both when registering an endpoint and on
every delivery attempt.

Regression coverage includes mixed public/nonpublic DNS responses, IPv6
translation and tunnel addresses, native public IPv6, and a public initial
503 response followed by a NAT64-private DNS answer on retry. The retry is
rejected without a second network send. These are adapter-level tests; real
provider routing, redirect behavior, and the broader release gates still need
their own evidence.
