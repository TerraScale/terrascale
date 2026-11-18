# Releasing the TerraBase image

TerraBase does not release on a push to `main`. A human selects an already-built image by dispatching the `release-only` job in `.github/workflows/ci.yml`; the hosts verify its signed selection and pull the image. This page does not record any commit as the live one. Read the running revision from the release job summary or from the service, not from this file. The TerraBase and ts-auth images run as uid 1654, and root is used only to create `/data`.

## Dispatch

Run both commands from a checkout that can dispatch `TerraScale/terrascale`. GitHub accepts a branch name as `--ref`. A raw commit SHA is rejected.

Publish the linux/arm64 image without releasing it:

```sh
gh workflow run ci.yml --repo TerraScale/terrascale --ref main -f publication=build-only
```

The `main` job summary prints one line:

```text
BUILD_ONLY terrabase revision=<sha> index=sha256:<index> arm64=sha256:<arm64>
```

`arm64` is the child digest. Release that digest to each region. The workflow checks that the image is `linux/arm64` and that `org.opencontainers.image.revision` equals the SHA of this run:

```sh
gh workflow run ci.yml --repo TerraScale/terrascale --ref main \
  -f publication=release-only \
  -f terrabase_arm64=sha256:<arm64>
```

`publication=release-only` runs only the `release-only` job. Before selecting an image, it requires the exact main revision to have qualified main-push CI, checks the native Linux/arm64 image and revision label, and verifies its main-workflow build provenance for that same revision. A digest built from another commit is refused.

The job signs the exact raw child manifest with a service-bound release selection, then attaches that bundle to all three production OCIR repositories, the San Jose staging repository and GHCR. After every attachment verifies, it downloads the current qualified `ci-*` orchestrator binary. Staging tag moves and staging waits are excluded until C28/C7 restore staging. Existing staging OCIR release pointers remain unchanged. The `STAGING:` message in `release-app.sh` marks the single insertion point for the future staging step; staging image and bundle publication remain unchanged.

The production sjc, fra and gru OCIR `:release` tags are selected concurrently. Each write is read back and checked against the same child digest; a mismatch reports the repository, expected digest and observed digest. Once every tag operation succeeds, the retained `ghcr.io/terrascale/terrabase:release` tag advances to the same manifest. The separate `Wait for rollout` step passes `terrabase@sha256:<arm64>` to one `release wait` covering all three production regions with its existing deadline. The orchestrator owns rollout order. The current wait reports a fleet-ready digest: during a partial rollout its `running` projection can omit the app even in a region already serving the new digest. Actual regional runtime diagnostics are accepted backlog work; the gap does not change the selected release or the wait's pass/fail result.

Each host verifies both build provenance and selection before pulling. Observations use authenticated HTTPS 443; CI does not push to hosts over NATS 4222. A failed observation or rollout fails the job; it does not dispatch another run. The default `publication=release` input does not release TerraBase.

## Recover a partial tag selection

Production tag writes are concurrent registry operations, not an atomic transaction. A failed write or read-back stops the job before GHCR advances or the production wait starts, but successful writes remain selected and their hosts may already be rolling out. A lost write response or failed read-back does not prove that the tag stayed on its old digest. Inspect every production `:release` tag and the actual running versions before deciding what happened.

After correcting the registry failure, rerun the original `release-only` job for the same qualified revision and immutable child digest so the selection step converges all pointers and runs the fleet wait. Do not infer fleet convergence from one region, undo successful writes automatically or reselect an older digest as compensation. If the new application fails, use the signed rollback procedure below. Auth and dashboard use a live-revision deployment gate and may skip selection on a workflow rerun when its probe already sees the target; a rerun alone is not proof that their regional tags converged.

Deploy the OCIR receiver from [ts-orchestrator #431](https://github.com/TerraScale/ts-orchestrator/pull/431) before using this producer. The receiver expands package-only references using its registry host, namespace and environment prefix and must understand signed selections and HTTPS observations; publishing a tag cannot upgrade an old receiver. TerraBase selection remains an explicit human release decision. See [regional publication](../dev/ocir-publication.md) for the bundle and registry contract.

## Roll back to the previous image

A tag change alone cannot authorize an older or held image: the immutable image, its build provenance and its release selection must agree on the exact revision and allowed main workflow. Do not reselect an older digest from the current main workflow, relabel it, or remove a receiver hold to force a rollback.

To restore earlier behavior, land a new main commit that restores it while preserving the current storage-format support and migration history. Qualify that exact commit through main-push CI, build its native image, and explicitly select its new digest using the dispatch above. This produces new build provenance and a new signed selection for the rollback build.

While a release is in progress, each region's orchestrator swaps to the new digest, waits until the region is ready, and rolls that region back to the image it was running if the release fails. A superseded release exits without installing the digest. Confirm the revision the service is actually running after the job finishes. Do not write that commit into this page.

## Going back after a storage migration

Every format change ships in two releases: first a build that supports the new format and still writes the old
one, then the change that raises Target. The automatic reinstall of the previous image after the second release
lands on a build that supports the migrated directory, so it simply runs. Never empty a data directory or discard
data in response to a refusal message. Going back two releases needs the stopped-node undo in
[Storage migrations](storage-migrations.md#going-back-two-releases). Watch the replica format and lag metrics
throughout a regional rollout.
