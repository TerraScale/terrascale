# syntax=docker/dockerfile:1
# CI runs the `test-libs`, `test-auth` and `test-terrabase` stages on pull requests and merge groups (.github/workflows/ci.yml);
# `make docker-test` builds `test`, all three. The `terrabase` and `ts-auth` stages publish to GHCR for ts-orch.
# ts-auth builds the in-repo SDK from source (named context `ts-dotnet`, sdk/dotnet); its tests start the TerraBase built from
# this same tree (Makefile TERRABASE_BINARY).

# Match global.json and keep the SDK's targeting packs aligned with locked NuGet dependencies.
# Build stages run on the builder's platform; the arm64 images cross-publish (publish-* stages).
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:11.0.100-rc.1-resolute@sha256:d0a39ca4acefe992f26030f8fb8c60e1b14ac088822cb3fdfbe536ecf843a675 AS sdk

FROM sdk AS base
# Test hosts retain the eight-CPU .NET budget across runner types.
# Publish/runtime stages have their own base and retain automatic CPU discovery.
ARG DOTNET_PROCESSOR_COUNT=8
ARG DOTNET_GCHeapCount=8
ENV DOTNET_PROCESSOR_COUNT=$DOTNET_PROCESSOR_COUNT DOTNET_GCHeapCount=$DOTNET_GCHeapCount
# The SDK image's ASPNETCORE_HTTP_PORTS=8080 makes Kestrel warn "Overriding address(es)", which tests that assert
# on warnings see; the services choose their own listeners.
# DOTNET_ROOT lets the framework-dependent `terrabase` apphost that ts-auth's tests start find the runtime.
# NUGET_SCRATCH holds NuGet's cross-process lock files. It is a shared cache mount, so concurrent builds on one BuildKit
# (parallel CI jobs) serialize package extraction into the shared packages cache instead of racing.
ENV CI=true DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 ASPNETCORE_HTTP_PORTS= DOTNET_ROOT=/usr/share/dotnet NUGET_SCRATCH=/nuget-scratch
# The WAL power-loss tests compile their LD_PRELOAD shim with cc and the C library headers.
RUN apt-get update \
    && apt-get install -y --no-install-recommends make nodejs npm jq gcc libc6-dev python3 \
    && rm -rf /var/lib/apt/lists/*
# Chromium for the ts-auth browser tests: the npm playwright matching Microsoft.Playwright in Directory.Packages.props
# installs the same browser revision. Redocly (auth-openapi-check) is fetched once here too.
RUN npx --yes playwright@1.63.0 install --with-deps chromium \
    && npx --yes @redocly/cli@2.57.0 --version \
    && rm -rf /var/lib/apt/lists/*
# .git is excluded: its packs and index differ per clone and would invalidate every later cached layer.
COPY --from=ts-dotnet --exclude=.git . /src/ts-dotnet
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

# One build and one test stage per area; CI runs the areas as parallel jobs. A build stage checks whitespace format,
# builds its projects in Release with analyzers as errors (libs.slnf, auth.slnf, terrabase.slnf) and runs its static
# checks: BuildKit reuses it only for byte-identical inputs. The test stage runs the area's tests on that build (its
# `dotnet build` calls reuse compiled outputs; `-o restore` retains the assets prepared by the build stage).
# CI passes no-cache-filters for test-<area>, so tests always execute. build-auth also builds the TerraBase that
# ts-auth's tests start (auth.slnf does not include it); publishing restores the SDK's linux-x64 assets.
FROM source AS build-libs
RUN make -o restore fmt-check libs-build SOLUTION=libs.slnf CONFIGURATION=Release

FROM build-libs AS test-libs
RUN make -o restore libs-check libs-test CONFIGURATION=Release && touch /passed

FROM source AS build-auth
ARG TS_AUTH_REVISION
# The advisory audit stays fresh in test-auth, outside this cacheable checked-build stage.
RUN make -o restore -o auth-audit fmt-check auth-check auth-build SOLUTION=auth.slnf CONFIGURATION=Release \
    && dotnet build src/TerraBase/TerraBase.csproj --configuration Release --no-restore --nologo \
    && make auth-publish RUNTIME=linux-x64 PUBLISH_DIR=/out

FROM build-auth AS test-auth
ARG TS_AUTH_REVISION
RUN make -o restore auth-audit auth-test-fast CONFIGURATION=Release && touch /passed

FROM source AS build-terrabase
# The commit /healthz reports as revision (MSBuild reads it from the environment); the image has no .git.
ARG TERRABASE_REVISION
RUN make -o restore fmt-check terrabase-check SOLUTION=terrabase.slnf CONFIGURATION=Release \
    && dotnet format whitespace src/TerraBase.Analyzers.BuildCheck/TerraBase.Analyzers.BuildCheck.csproj --verify-no-changes --no-restore \
    && dotnet publish src/TerraBase --configuration Release --runtime linux-x64 --self-contained --no-restore --output /out

FROM build-terrabase AS test-terrabase
ARG TERRABASE_REVISION
# Finish Fast's incremental builds before the independent E2E processes read those outputs.
RUN make -o restore terrabase-test-fast CONFIGURATION=Release \
    && scripts/vendor/test-turso-native-singlefile.sh \
    && make -j2 -o restore -o terrabase-build terrabase-e2e-local CONFIGURATION=Release \
    && touch /passed

# All three areas (BuildKit runs them in parallel): `make docker-test`.
FROM scratch AS test
COPY --from=test-libs /passed /libs
COPY --from=test-auth /passed /auth
COPY --from=test-terrabase /passed /terrabase

# Slow exports each suite's log and exit code for CI to report; `test-slow` exports both area directories.
# Plain text: TUnit colours its output here, and the report parses lines like "failed <test> (<duration>)".
# Each area runs independently so an auth failure cannot prevent TerraBase Slow from executing.
# Slow compiles its test graph without repeating Fast's whitespace, security, OpenAPI or packaging checks.
FROM source AS slow-auth-build
ARG TS_AUTH_REVISION
RUN make -o restore auth-build CONFIGURATION=Release \
    && dotnet build src/TerraBase/TerraBase.csproj --configuration Release --no-restore --nologo

FROM slow-auth-build AS slow-auth-run
ARG TS_AUTH_REVISION
RUN mkdir /slow && { make -o restore auth-test-slow CONFIGURATION=Release TEST_ARGS=--no-ansi; echo $? > /slow/exit; } 2>&1 | tee /slow/log

FROM scratch AS test-slow-auth
COPY --from=slow-auth-run /slow /

FROM source AS slow-terrabase-build
ARG TERRABASE_REVISION
RUN make -o restore terrabase-build CONFIGURATION=Release

FROM slow-terrabase-build AS slow-terrabase-run
ARG TERRABASE_REVISION
# BuildKit secret mounts never enter a layer or build arg. No org-level provisioning credential reaches CI.
RUN --mount=type=secret,id=tigris-ci,required=true \
    mkdir /slow && { set -a; . /run/secrets/tigris-ci; set +a; \
    make -o restore terrabase-test-slow CONFIGURATION=Release TEST_ARGS=--no-ansi; echo $? > /slow/exit; } 2>&1 | tee /slow/log

FROM scratch AS test-slow-terrabase
COPY --from=slow-terrabase-run /slow /

FROM scratch AS test-slow
COPY --from=slow-auth-run /slow /auth/
COPY --from=slow-terrabase-run /slow /terrabase/

# Production publication compiles only its service graph, once per architecture. Checked builds above retain
# pre-merge packaging validation; main does not repeat whitespace, analyzer-negative or OpenAPI checks.
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

# TerraBase stores its volume at /data.
FROM mcr.microsoft.com/dotnet/runtime-deps:11.0-resolute-chiseled AS terrabase
COPY --from=publish-terrabase /out /app
USER 0
# The base image's ASPNETCORE_HTTP_PORTS=8080 only makes Kestrel warn: the services bind their own listeners.
ENV ASPNETCORE_HTTP_PORTS=
WORKDIR /data
ENTRYPOINT ["/app/terrabase"]

FROM mcr.microsoft.com/dotnet/runtime-deps:11.0-resolute-chiseled AS ts-auth
COPY --from=publish-ts-auth /out /app
USER 0
ENV ASPNETCORE_HTTP_PORTS=
ENTRYPOINT ["/app/ts-auth"]

# The single-file TerraBase executable CI attaches to a GitHub release on main (ts-go and ts-status tests start it).
FROM scratch AS terrabase-binary
COPY --from=publish-terrabase /out/terrabase /terrabase-linux-amd64
