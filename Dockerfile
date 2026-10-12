# syntax=docker/dockerfile:1
# Fly builds this image (fly.toml). Tests run against the source tree, not in the image:
# make build lint test-fast.

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
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish src/TerraScale.Dashboard --configuration Release --output /app \
      -p:RestoreLockedMode=true -p:ContinuousIntegrationBuild=true

# No shell or package manager; runs as the non-root app user and listens on 8080.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled
WORKDIR /app
COPY --from=publish /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "TerraScale.Dashboard.dll"]
