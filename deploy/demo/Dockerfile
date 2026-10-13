# syntax=docker/dockerfile:1
# The public synthetic demo (temporarily on ts-dashboard). Build from the repo root:
#   fly deploy --config deploy/demo/fly.toml --dockerfile deploy/demo/Dockerfile --build-arg SOURCE_REVISION=$(git rev-parse HEAD)

FROM node:24-bookworm-slim AS node-modules
WORKDIR /src/src/TerraScale.Dashboard/web
COPY src/TerraScale.Dashboard/web/package.json src/TerraScale.Dashboard/web/package-lock.json src/TerraScale.Dashboard/web/.npmrc ./
RUN --mount=type=cache,target=/root/.npm \
    npm ci --ignore-scripts --no-audit --no-fund --prefer-offline

FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble AS publish
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
COPY --from=node-modules /usr/local/bin/node /usr/local/bin/node
WORKDIR /src
COPY --from=node-modules /src/src/TerraScale.Dashboard/web/node_modules ./src/TerraScale.Dashboard/web/node_modules
COPY . .
ARG SOURCE_REVISION
RUN test -n "$SOURCE_REVISION" || { echo 'Pass --build-arg SOURCE_REVISION=$(git rev-parse HEAD).' >&2; exit 1; }
# The repository holds one application project (scripts/verify-sdk.py); like scripts/demo.sh, materialize the
# test-only demo host project here. Its content root (web/static) is <root>/src/TerraScale.Dashboard.
COPY <<'CSPROJ' deploy/demo/TerraScale.Dashboard.Demo.csproj
<Project Sdk="Microsoft.NET.Sdk.Web">
  <ItemGroup>
    <ProjectReference Include="../../src/TerraScale.Dashboard/TerraScale.Dashboard.csproj" />
    <Compile Include="../../tests/TerraScale.Dashboard.E2E/Demo/**/*.cs" />
  </ItemGroup>
</Project>
CSPROJ
# The build context keeps the checkout's file modes (umask 077 yields 0600); the non-root app user must read them.
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish src/TerraScale.Dashboard --configuration Release --output /demo/src/TerraScale.Dashboard \
      -p:RestoreLockedMode=true -p:SourceRevisionId=$SOURCE_REVISION \
    && dotnet publish deploy/demo --configuration Release --output /demo/bin -p:SourceRevisionId=$SOURCE_REVISION \
    && chmod -R a+rX /demo

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled
COPY --from=publish /demo /demo
EXPOSE 8080
ENTRYPOINT ["dotnet", "/demo/bin/TerraScale.Dashboard.Demo.dll", "/demo"]
