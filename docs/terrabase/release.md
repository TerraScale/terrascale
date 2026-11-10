# Releasing the TerraBase image

TerraBase does not release on a push to `main`. The `Release to every region` step in `.github/workflows/ci.yml` skips `terrabase` while the fleet still runs executors with no fence. A human ships the image by dispatching that same workflow. This page does not record any commit as the live one. Read the running revision from the release job summary or from the service, not from this file. The TerraBase and ts-auth images run as uid 1654, and root is used only to create `/data`.

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

`publication=release-only` runs only the `release-only` job. It downloads the current `ci-*` orchestrator binary and calls `release app --service terrabase` once per region. A failed region does not dispatch another run. The default `publication=release` input does not release TerraBase.

## Roll back to the previous image

Keep the `arm64` digest from the last good `BUILD_ONLY` line (or inspect `ghcr.io/terrascale/terrabase@sha256:…`). Dispatch `release-only` again with that digest, on a `--ref` whose workflow SHA matches the image revision label. The job refuses a digest built from a different commit.

While a release is in progress, each region's orchestrator swaps to the new digest, waits until the region is ready, and rolls that region back to the image it was running if the release fails. A superseded release exits without installing the digest. Confirm the revision the service is actually running after the job finishes. Do not write that commit into this page.

A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.
