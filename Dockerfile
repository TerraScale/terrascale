# syntax=docker/dockerfile:1
# CI runs the `test` stage on every pull request and push (.github/workflows/ci.yml); `make docker-test` runs the same
# build locally. The `terrabase` and `ts-auth` stages are the images CI deploys to Fly.
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

# Fly images: framework-dependent on the chiseled ASP.NET runtime (no shell, no package manager), so a deploy pushes only
# the ~20 MB app layer; the base layer is pushed once per app. These stages skip the test stages: the pull request's
# `test` check gates the merge, and main's `test` runs next to the deploy. Run admin commands with
# `fly ssh console -C '/app/<binary> ...'`. root: Fly mounts volumes root-owned.
FROM mcr.microsoft.com/dotnet/sdk:11.0 AS publish-terrabase
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
WORKDIR /src/terrascale
COPY . .
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish src/TerraBase -c Release -r linux-x64 --no-self-contained -p:PublishSingleFile=false -o /out

FROM mcr.microsoft.com/dotnet/sdk:11.0 AS publish-ts-auth
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
COPY --from=ts-dotnet . /src/ts-dotnet
WORKDIR /src/terrascale
COPY . .
# Shown by ts-auth's /healthz as <version>+<revision>.
ARG TS_AUTH_REVISION
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish src/TerraScale.Authentication -c Release -r linux-x64 --no-self-contained -o /out

# Fly mounts TerraBase's volume at /data.
FROM mcr.microsoft.com/dotnet/aspnet:11.0-resolute-chiseled AS terrabase
COPY --from=publish-terrabase /out /app
USER 0
# The base image's ASPNETCORE_HTTP_PORTS=8080 only makes Kestrel warn: the services bind their own listeners.
ENV ASPNETCORE_HTTP_PORTS=
WORKDIR /data
ENTRYPOINT ["/app/terrabase"]

FROM mcr.microsoft.com/dotnet/aspnet:11.0-resolute-chiseled AS ts-auth
COPY --from=publish-ts-auth /out /app
USER 0
ENV ASPNETCORE_HTTP_PORTS=
ENTRYPOINT ["/app/ts-auth"]
