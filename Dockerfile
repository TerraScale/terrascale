# syntax=docker/dockerfile:1
# Fly builds the final stage (fly.toml). CI builds `test-slow` on the alpha runners' BuildKit, because the
# browser journeys need Chromium's system libraries; `make test-fast` runs on the runner itself.

FROM node:24-bookworm-slim AS node-modules
WORKDIR /src
COPY package.json package-lock.json .npmrc ./
RUN --mount=type=cache,target=/root/.npm \
    npm ci --ignore-scripts --no-audit --no-fund --prefer-offline

# The SDK pinned by global.json.
FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble AS publish
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
# The project build bundles CSS and JS with node (BundleDashboardAssets).
COPY --from=node-modules /usr/local/bin/node /usr/local/bin/node
WORKDIR /src
COPY --from=node-modules /src/node_modules ./node_modules
COPY . .
# The deployed commit (`--build-arg SOURCE_REVISION=$(git rev-parse HEAD)`): /health/ready reports it.
ARG SOURCE_REVISION
RUN test -n "$SOURCE_REVISION" || { echo 'Pass --build-arg SOURCE_REVISION=$(git rev-parse HEAD).' >&2; exit 1; }
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish src/TerraScale.Dashboard --configuration Release --output /app \
      -p:RestoreLockedMode=true -p:ContinuousIntegrationBuild=true -p:SourceRevisionId=$SOURCE_REVISION

# The browser journeys (`make test-slow`) with Chromium and its system libraries. Never deployed.
FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble AS test-browsers
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
COPY --from=node-modules /usr/local/bin/node /usr/local/bin/node
WORKDIR /src
COPY --from=node-modules /src/node_modules ./node_modules
# The browser revision matching Microsoft.Playwright and playwright-core 1.63.0.
RUN apt-get update && apt-get install -y --no-install-recommends make \
    && node node_modules/playwright-core/cli.js install --with-deps chromium \
    && rm -rf /var/lib/apt/lists/*

FROM test-browsers AS test-slow
COPY . .
RUN make test-slow

# No shell or package manager; runs as the non-root app user and listens on 8080.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled
WORKDIR /app
COPY --from=publish /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "TerraScale.Dashboard.dll"]
