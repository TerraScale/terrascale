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
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:11.0.100-rc.1-resolute@sha256:d0a39ca4acefe992f26030f8fb8c60e1b14ac088822cb3fdfbe536ecf843a675 AS publish
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
FROM mcr.microsoft.com/dotnet/aspnet:11.0.0-rc.3-resolute-chiseled@sha256:1431d55bf5bd5708450c10664f3d4c4603d7e60cc059e64397787de3978f3e8c
WORKDIR /app
COPY --from=publish /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "TerraScale.Dashboard.dll"]
