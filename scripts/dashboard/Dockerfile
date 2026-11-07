# syntax=docker/dockerfile:1
# The final stage is the production application; synthetic providers exist only in the test executable.

# Build stages run on the builder's platform; the output is portable IL, so one publish serves amd64 and arm64.
FROM --platform=$BUILDPLATFORM node:24-bookworm-slim AS node-modules
# The asset build's npm project.
WORKDIR /src/src/TerraScale.Dashboard/web
COPY src/TerraScale.Dashboard/web/package.json src/TerraScale.Dashboard/web/package-lock.json src/TerraScale.Dashboard/web/.npmrc ./
RUN --mount=type=cache,target=/root/.npm \
    npm ci --ignore-scripts --no-audit --no-fund --prefer-offline

# The SDK pinned by global.json.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:11.0.100-rc.3-resolute@sha256:3a9175b6c54b2f076817af1360f691c9994447b192faa48a1a1f112a516844c7 AS publish
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
# The project build bundles CSS and JS with node (BundleDashboardAssets).
COPY --from=node-modules /usr/local/bin/node /usr/local/bin/node
WORKDIR /src
COPY --from=node-modules /src/src/TerraScale.Dashboard/web/node_modules ./src/TerraScale.Dashboard/web/node_modules
COPY NuGet.config Directory.Build.props Directory.Build.targets Directory.Packages.props BannedSymbols.txt global.json ./
COPY sdk/dotnet/ ./sdk/dotnet/
COPY src/TerraScale.Dashboard/TerraScale.Dashboard.csproj src/TerraScale.Dashboard/packages.lock.json ./src/TerraScale.Dashboard/
# Keep restored package bytes in this cached build layer, including on a fresh CI builder.
RUN dotnet restore src/TerraScale.Dashboard/TerraScale.Dashboard.csproj --locked-mode
COPY . .
# Pin the source revision in the build; the image label and deployment digest identify the serving revision.
ARG SOURCE_REVISION
RUN test -n "$SOURCE_REVISION" || { echo 'Pass --build-arg SOURCE_REVISION=$(git rev-parse HEAD).' >&2; exit 1; }
RUN dotnet publish src/TerraScale.Dashboard --configuration Release --output /app --no-restore \
      -p:ContinuousIntegrationBuild=true -p:SourceRevisionId=$SOURCE_REVISION

# No shell or package manager; runs as the non-root app user and listens on 8080.
FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-rc.3-resolute-chiseled@sha256:784ce4a2e816a0e93dc72ceb81d2401eed8f27fdb44610a3e7ceb6a5f16bba70
WORKDIR /app
COPY --from=publish /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "TerraScale.Dashboard.dll"]
