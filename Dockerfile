# syntax=docker/dockerfile:1

# Match global.json and keep the SDK's targeting packs aligned with locked NuGet dependencies.
# Build stages run on the builder's platform; the arm64 images cross-publish (publish-* stages).
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:11.0.100-resolute@sha256:65f3c7c6a25a239ab3e6719f2896147e7b68242ff2d8818dd70498bde6ef8bea AS sdk

FROM sdk AS base
# NUGET_SCRATCH holds NuGet's cross-process lock files. It is a shared cache mount, so concurrent builds on one BuildKit
# (parallel CI jobs) serialize package extraction into the shared packages cache instead of racing.
ENV CI=true DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 NUGET_SCRATCH=/nuget-scratch
RUN apt-get update \
    && apt-get install -y --no-install-recommends make \
    && rm -rf /var/lib/apt/lists/*
# .git is excluded: its packs and index differ per clone and would invalidate every later cached layer.
COPY --from=ts-dotnet --exclude=.git . /src/terrascale/sdk/dotnet
ENV TS_DOTNET_DIR=sdk/dotnet TsDotnetRoot=/src/terrascale/sdk/dotnet
WORKDIR /src/terrascale

# COPY from this filtered stage keys restore on manifest contents, not changes to application source.
FROM base AS restore-inputs
COPY . /restore-inputs
RUN mkdir /restore-manifests \
    && cd /restore-inputs \
    && find . -type f \( -name '*.csproj' -o -name '*.props' -o -name '*.targets' \
        -o -name '*.slnx' -o -name '*.slnf' -o -name '*packages.lock.json' \
        -o -name 'NuGet.config' -o -name 'global.json' -o -name 'dotnet-tools.json' \
        -o -name 'Makefile' -o -path './scripts/dashboard/check-stripe-dependency.py' \) -exec cp --parents '{}' /restore-manifests/ \;

# Shared packages are restored into this layer (and deduplicated across concurrent CI jobs): a BuildKit
# cache mount is best-effort and may come up empty in a later stage, so it only holds NuGet's download cache.
FROM base AS restored
COPY --from=restore-inputs /restore-manifests/ .
RUN --mount=type=cache,target=/root/.local/share/NuGet/http-cache,id=nuget-http \
    --mount=type=cache,target=/nuget-scratch,id=nuget-scratch \
    make restore

FROM restored AS source
COPY . .
# CI supplies both pinned release architectures; cached bytes are verified again inside the build.
RUN scripts/vendor/verify-turso-native.sh src/vendor/Turso.Data.Sqlite/native

FROM source AS publish-terrabase
ARG TARGETARCH TERRABASE_REVISION
RUN case "$TARGETARCH" in amd64) runtime=linux-x64; machine=3e00 ;; arm64) runtime=linux-arm64; machine=b700 ;; *) exit 1 ;; esac \
    && make terrabase-publish RUNTIME="$runtime" PUBLISH_DIR=/out \
    && test "$(od -An -tx1 -j18 -N2 /out/terrabase | tr -d ' \n')" = "$machine"

FROM source AS publish-ts-auth
ARG TARGETARCH TS_AUTH_REVISION
RUN case "$TARGETARCH" in amd64) runtime=linux-x64; machine=3e00 ;; arm64) runtime=linux-arm64; machine=b700 ;; *) exit 1 ;; esac \
    && make auth-publish RUNTIME="$runtime" PUBLISH_DIR=/out \
    && test "$(od -An -tx1 -j18 -N2 /out/ts-auth | tr -d ' \n')" = "$machine"

FROM mcr.microsoft.com/dotnet/runtime-deps:11.0.0-resolute-chiseled@sha256:24534e1f18024d3f8026061537fa21ba424f46cc0cdab4297c5528937b1ae960 AS terrabase
COPY --from=publish-terrabase /out /app
USER 0
RUN --mount=type=bind,from=busybox:1.36.1-musl@sha256:3c6ae8008e2c2eedd141725c30b20d9c36b026eb796688f88205845ef17aa213,source=/bin/busybox,target=/busybox ["/busybox","mkdir","-p","/data"]
RUN --mount=type=bind,from=busybox:1.36.1-musl@sha256:3c6ae8008e2c2eedd141725c30b20d9c36b026eb796688f88205845ef17aa213,source=/bin/busybox,target=/busybox ["/busybox","chown","1654:1654","/data"]
ENV ASPNETCORE_HTTP_PORTS=
WORKDIR /data
USER 1654
ENTRYPOINT ["/app/terrabase"]

FROM mcr.microsoft.com/dotnet/runtime-deps:11.0.0-resolute-chiseled@sha256:24534e1f18024d3f8026061537fa21ba424f46cc0cdab4297c5528937b1ae960 AS ts-auth
COPY --from=publish-ts-auth /out /app
USER 0
RUN --mount=type=bind,from=busybox:1.36.1-musl@sha256:3c6ae8008e2c2eedd141725c30b20d9c36b026eb796688f88205845ef17aa213,source=/bin/busybox,target=/busybox ["/busybox","mkdir","-p","/data"]
RUN --mount=type=bind,from=busybox:1.36.1-musl@sha256:3c6ae8008e2c2eedd141725c30b20d9c36b026eb796688f88205845ef17aa213,source=/bin/busybox,target=/busybox ["/busybox","chown","1654:1654","/data"]
ENV ASPNETCORE_HTTP_PORTS=
USER 1654
ENTRYPOINT ["/app/ts-auth"]
