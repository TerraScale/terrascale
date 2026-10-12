# syntax=docker/dockerfile:1
# CI runs the `test-libs`, `test-auth` and `test-terrabase` stages on pull requests and pushes (.github/workflows/ci.yml);
# `make docker-test` builds `test`, all three. The `terrabase` and `ts-auth` stages are the images CI deploys to Fly.
# ts-auth builds ts-dotnet from source (named context `ts-dotnet`, a checkout); its tests start the TerraBase built from
# this same tree (Makefile TERRABASE_BINARY).

FROM mcr.microsoft.com/dotnet/sdk:11.0 AS base
# The SDK image's ASPNETCORE_HTTP_PORTS=8080 makes Kestrel warn "Overriding address(es)", which tests that assert
# on warnings see; the services choose their own listeners.
# DOTNET_ROOT lets the framework-dependent `terrabase` apphost that ts-auth's tests start find the runtime.
ENV CI=true DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 ASPNETCORE_HTTP_PORTS= DOTNET_ROOT=/usr/share/dotnet
RUN apt-get update \
    && apt-get install -y --no-install-recommends make nodejs npm jq \
    && rm -rf /var/lib/apt/lists/*
# Chromium for the ts-auth browser tests: the npm playwright matching Microsoft.Playwright in Directory.Packages.props
# installs the same browser revision. Redocly (auth-openapi-check) is fetched once here too.
RUN npx --yes playwright@1.63.0 install --with-deps chromium \
    && npx --yes @redocly/cli@2.57.0 --version \
    && rm -rf /var/lib/apt/lists/*
# Per-job Git metadata changes even at the pinned SDK revision and invalidates every preparation layer.
COPY --from=ts-dotnet --exclude=.git . /src/ts-dotnet
WORKDIR /src/terrascale

FROM base AS source
COPY . .

# Preparation can be reused for unchanged inputs; CI always reruns the test-* stages with no-cache-filters.
FROM source AS check-libs
RUN --mount=type=cache,target=/root/.nuget/packages \
    make fmt-check libs-check CONFIGURATION=Release

FROM check-libs AS test-libs
RUN --mount=type=cache,target=/root/.nuget/packages \
    make libs-test CONFIGURATION=Release && touch /passed

FROM source AS check-auth
RUN --mount=type=cache,target=/root/.nuget/packages \
    make fmt-check auth-openapi-check CONFIGURATION=Release

FROM check-auth AS test-auth
RUN --mount=type=cache,target=/root/.nuget/packages \
    make auth-audit auth-test-fast CONFIGURATION=Release && touch /passed

FROM source AS check-terrabase
RUN --mount=type=cache,target=/root/.nuget/packages \
    make fmt-check terrabase-check CONFIGURATION=Release

FROM check-terrabase AS test-terrabase
RUN --mount=type=cache,target=/root/.nuget/packages \
    make terrabase-test-fast CONFIGURATION=Release && touch /passed

# All three areas (BuildKit runs them in parallel): `make docker-test`.
FROM scratch AS test
COPY --from=test-libs /passed /libs
COPY --from=test-auth /passed /auth
COPY --from=test-terrabase /passed /terrabase

# The Slow suites never fail the build: their log and exit code are the `test-slow` output, which CI reports from.
# Plain text: TUnit colours its output here, and the report parses lines like "failed <test> (<duration>)".
FROM source AS slow-run
RUN --mount=type=cache,target=/root/.nuget/packages \
    mkdir /slow && { make test-slow CONFIGURATION=Release TEST_ARGS=--no-ansi; echo $? > /slow/exit; } 2>&1 | tee /slow/log

FROM scratch AS test-slow
COPY --from=slow-run /slow /

# Fly images: the self-contained release executables (`make terrabase-publish` / `auth-publish` flags) on the chiseled
# runtime-deps base (6 MB: no shell, no package manager). These stages skip the test stages: CI deploys only a tree whose
# `test` passed (scripts/fly/fast-gate.sh). Run admin commands with `fly ssh console -C '/app/<binary> ...'`.
# root: Fly mounts volumes root-owned.
FROM mcr.microsoft.com/dotnet/sdk:11.0 AS publish-terrabase
# CI=true turns on locked-mode restore (Directory.Build.props).
ENV CI=true DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
WORKDIR /src/terrascale
COPY . .
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish src/TerraBase --configuration Release --runtime linux-x64 --self-contained --output /out

FROM mcr.microsoft.com/dotnet/sdk:11.0 AS publish-ts-auth
ENV CI=true DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
COPY --from=ts-dotnet . /src/ts-dotnet
WORKDIR /src/terrascale
COPY . .
# Shown by ts-auth's /healthz as <version>+<revision>.
ARG TS_AUTH_REVISION
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish src/TerraScale.Authentication --configuration Release --runtime linux-x64 --self-contained --output /out

# Fly mounts TerraBase's volume at /data.
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
