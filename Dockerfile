# syntax=docker/dockerfile:1
# CI runs the `test-libs`, `test-auth` and `test-terrabase` stages on pull requests and pushes (.github/workflows/ci.yml);
# `make docker-test` builds `test`, all three. The `terrabase` and `ts-auth` stages are the images CI deploys to Fly.
# ts-auth builds ts-dotnet from source and its tests start the pinned TerraBase release binary that
# ts-dotnet's TerraBaseTestServer verifies, so the build takes two named contexts: `ts-dotnet` (a checkout) and
# `terrabase-pinned` (a directory holding terrabase-linux-amd64).

FROM mcr.microsoft.com/dotnet/sdk:11.0 AS base
# The SDK image's ASPNETCORE_HTTP_PORTS=8080 makes Kestrel warn "Overriding address(es)", which tests that assert
# on warnings see; the services choose their own listeners.
ENV CI=true DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 ASPNETCORE_HTTP_PORTS=
RUN apt-get update \
    && apt-get install -y --no-install-recommends make nodejs npm \
    && rm -rf /var/lib/apt/lists/*
# Chromium for the ts-auth browser tests: the npm playwright matching Microsoft.Playwright in Directory.Packages.props
# installs the same browser revision. Redocly (auth-openapi-check) is fetched once here too.
RUN npx --yes playwright@1.63.0 install --with-deps chromium \
    && npx --yes @redocly/cli@2.57.0 --version \
    && rm -rf /var/lib/apt/lists/*
COPY --from=ts-dotnet . /src/ts-dotnet
COPY --from=terrabase-pinned --chmod=755 terrabase-linux-amd64 /opt/terrabase-pinned/terrabase-linux-amd64
ENV TERRABASE_BINARY=/opt/terrabase-pinned/terrabase-linux-amd64
WORKDIR /src/terrascale

FROM base AS source
COPY . .

# One stage per area; CI runs them as parallel jobs. Each checks whitespace format, builds its projects in Release with
# analyzers as errors (libs.slnf, auth.slnf, terrabase.slnf), runs its checks and its Fast suite.
FROM source AS test-libs
RUN --mount=type=cache,target=/root/.nuget/packages \
    make fmt-check libs-check libs-test CONFIGURATION=Release && touch /passed

FROM source AS test-auth
RUN --mount=type=cache,target=/root/.nuget/packages \
    make fmt-check auth-check auth-test-fast CONFIGURATION=Release && touch /passed

FROM source AS test-terrabase
RUN --mount=type=cache,target=/root/.nuget/packages \
    make fmt-check terrabase-check terrabase-test-fast CONFIGURATION=Release && touch /passed

# All three areas (BuildKit runs them in parallel): `make docker-test`.
FROM scratch AS test
COPY --from=test-libs /passed /libs
COPY --from=test-auth /passed /auth
COPY --from=test-terrabase /passed /terrabase

FROM source AS test-slow
RUN --mount=type=cache,target=/root/.nuget/packages \
    make test-slow CONFIGURATION=Release

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
