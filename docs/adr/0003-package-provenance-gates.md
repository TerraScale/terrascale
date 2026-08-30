# ADR 0003: qualify the exact package set by provenance

- Status: Accepted for the current release workflow
- Date: 2026-08-29
- Scope: `TerraScale.DotNet` and `TerraScale.EntityFrameworkCore` package delivery

## Context

The package verifier and Forgejo workflow already distinguish pull-request
validation from protected delivery. A build passing in the checkout does not
prove that the package later installed by a consumer is the same source that
was reviewed. A dirty source tree, archive path abuse, metadata spoofing, or a
changed release ref must fail closed.

## Decision

Production package promotion consumes one immutable
`TerraScale.DotNet.PackageSetHandoff/v2` record. It contains exactly the SDK and
EF Core package identities, versions, paths, and SHA-256 digests plus their one
shared source commit, source ref, and source tree. The protected workflow must
verify all of these against the checked-out `origin/develop` source before
archive inspection and clean-consumer installation.

Archive inspection also rejects API-version literals that are absent from the
accepted public contract. This prevents a compiled package from restoring an
unpublished route that the source-level route matrix does not bind.

The producer contract is the checked-in
[`terrascale-dotnet-package-set-handoff-v2.schema.json`](../contracts/terrascale-dotnet-package-set-handoff-v2.schema.json).
Unknown fields, missing package members, duplicate identities, paths other than
the two exact sibling archive filenames, noncanonical digests, and any source
ref other than `refs/heads/develop` fail closed. Relative sibling names keep the
three-file transfer portable without permitting traversal or rebinding it to a
different runner path.

The `.terrascale/deploy.json` build target `dotnet-package-set-v2` is the
protected producer. Its output root is
`artifacts/candidate/dotnet-package-set-v2`, and it emits exactly the manifest,
`TerraScale.DotNet.0.1.0.nupkg`, and
`TerraScale.EntityFrameworkCore.0.1.0.nupkg`. Deploy's build receipt binds that
three-file tree before transfer.

The verifier restores the EF package from an isolated source containing only
the two supplied first-party archives. It proves that NuGet consumed those
exact bytes, validates the declared dependency graph, builds a `net10.0`
consumer with warnings as errors, treats vulnerability-audit warnings from low
severity upward as failures, and records the MIT license disposition for the
resolved dependency closure. Its single receipt binds both packages; separate
receipts that could name different source candidates are not equivalent. The
receipt also hashes the v2 handoff itself, so a manifest substitution cannot
retain the same package-set evidence identity.

No workflow or documentation may describe a compile-only result as production
qualification. Live API compatibility, tenant isolation, reactive conformance,
publication, and operational gates remain separate evidence requirements.

## Consequences

The package producer and protected delivery handoff must coordinate both exact
artifacts. Rebuilding either during publication is not allowed because it would
break the source/digest identity. A no-argument package-set verification remains
useful for a clean local checkout but is rejected from a dirty tree. Both
packages still declare version `0.1.0`; release ownership must explicitly admit
the versions and publication destination before customer release.
