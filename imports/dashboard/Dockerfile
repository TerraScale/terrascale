# syntax=docker/dockerfile:1
# The final stage is the actual application. Only DEMO=true selects its in-memory demo providers.

# Build stages run on the builder's platform; the output is portable IL, so one publish serves amd64 and arm64.
FROM --platform=$BUILDPLATFORM node:24-bookworm-slim AS node-modules
# The asset build's npm project.
WORKDIR /src/src/TerraScale.Dashboard/web
COPY src/TerraScale.Dashboard/web/package.json src/TerraScale.Dashboard/web/package-lock.json src/TerraScale.Dashboard/web/.npmrc ./
RUN --mount=type=cache,target=/root/.npm \
    npm ci --ignore-scripts --no-audit --no-fund --prefer-offline

# The SDK pinned by global.json.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0.401-noble AS publish
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
# The project build bundles CSS and JS with node (BundleDashboardAssets).
COPY --from=node-modules /usr/local/bin/node /usr/local/bin/node
WORKDIR /src
COPY --from=node-modules /src/src/TerraScale.Dashboard/web/node_modules ./src/TerraScale.Dashboard/web/node_modules
COPY NuGet.Config Directory.Build.props Directory.Packages.props global.json ./
COPY vendor/dotnet/ ./vendor/dotnet/
COPY src/TerraScale.Dashboard/TerraScale.Dashboard.csproj src/TerraScale.Dashboard/packages.lock.json ./src/TerraScale.Dashboard/
# Keep restored package bytes in this cached build layer, including on a fresh CI builder.
RUN dotnet restore src/TerraScale.Dashboard/TerraScale.Dashboard.csproj --locked-mode
COPY . .
# The deployed commit (`--build-arg SOURCE_REVISION=$(git rev-parse HEAD)`): /health/ready reports it.
ARG SOURCE_REVISION
RUN test -n "$SOURCE_REVISION" || { echo 'Pass --build-arg SOURCE_REVISION=$(git rev-parse HEAD).' >&2; exit 1; }
RUN dotnet publish src/TerraScale.Dashboard --configuration Release --output /app --no-restore \
      -p:ContinuousIntegrationBuild=true -p:SourceRevisionId=$SOURCE_REVISION

# No shell or package manager; runs as the non-root app user and listens on 8080.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled@sha256:c89a6a61f494f20a5ae302990e814627a8d91b332fd4a8e7c1707c2d4da9555d
WORKDIR /app
COPY --from=publish /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "TerraScale.Dashboard.dll"]
