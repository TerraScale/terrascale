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

The job signs a service-bound release selection for that immutable digest, then moves the existing `ghcr.io/terrascale/terrabase:release` tag to the selected manifest without changing its digest. Each host verifies both the build provenance and the signed selection before pulling. The script downloads the current qualified `ci-*` orchestrator binary and uses `release wait` to read authenticated observations on HTTPS 443 until the selected image is serving in every placement region. CI does not push to hosts over NATS 4222. A failed observation or rollout fails the job; it does not dispatch another run. The default `publication=release` input does not release TerraBase.

Deploy the receiver from [ts-orchestrator #403](https://github.com/TerraScale/ts-orchestrator/pull/403) before using this producer. The receiver must understand signed selections and HTTPS observations; publishing a tag cannot upgrade an old receiver. TerraBase selection remains an explicit human release decision.

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
