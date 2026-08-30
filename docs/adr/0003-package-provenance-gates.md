# ADR 0003: qualify the exact package artifact by provenance

- Status: Accepted for the current release workflow
- Date: 2026-08-29
- Scope: `TerraScale.DotNet` package delivery

## Context

The package verifier and Forgejo workflow already distinguish pull-request
validation from protected delivery. A build passing in the checkout does not
prove that the package later installed by a consumer is the same source that
was reviewed. A dirty source tree, archive path abuse, metadata spoofing, or a
changed release ref must fail closed.

## Decision

Production package promotion consumes one immutable handoff containing the
package path, SHA-256 digest, source commit, source ref, and source tree. The
protected workflow must verify all of these against the checked-out
`origin/develop` source before archive inspection and clean-consumer
installation. The verifier remains the authority for archive safety, repository
metadata identity, package dependency isolation, and its emitted verification
receipt.

No workflow or documentation may describe a compile-only result as production
qualification. Live API compatibility, tenant isolation, reactive conformance,
publication, and operational gates remain separate evidence requirements.

## Consequences

The package producer and the protected delivery handoff must coordinate an
exact artifact. Rebuilding during publication is not allowed because it would
break the source/digest identity. A no-argument pack remains useful for a clean
local checkout but is rejected from a dirty tree. The current package metadata
still declares version `0.1.0`; release ownership must explicitly admit the
version and publication destination before customer release.
