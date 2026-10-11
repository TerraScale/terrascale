# syntax=docker/dockerfile:1
# CI runs the `test` stage on every pull request and push (.github/workflows/ci.yml); `make docker-test` runs the same
# build locally. ts-auth builds ts-dotnet from source and its tests start the pinned TerraBase release binary that
# ts-dotnet's TerraBaseTestServer verifies, so the build takes two named contexts: `ts-dotnet` (a checkout) and
# `terrabase-pinned` (a directory holding terrabase-linux-amd64).

FROM mcr.microsoft.com/dotnet/sdk:11.0 AS base
ENV CI=true DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
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

# Format, Release build with analyzers as errors, and the libs, auth and TerraBase checks.
FROM base AS build
COPY . .
RUN --mount=type=cache,target=/root/.nuget/packages \
    make lint libs-check auth-check terrabase-check CONFIGURATION=Release

FROM build AS test
RUN --mount=type=cache,target=/root/.nuget/packages \
    make test-fast CONFIGURATION=Release

FROM build AS test-slow
RUN --mount=type=cache,target=/root/.nuget/packages \
    make test-slow CONFIGURATION=Release
