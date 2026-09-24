FROM node:24-alpine AS css
WORKDIR /src
COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY scripts/build-css.mjs scripts/
COPY src/TerraScale.Dashboard/Styles src/TerraScale.Dashboard/Styles
COPY src/TerraScale.Dashboard/Features src/TerraScale.Dashboard/Features
RUN npm run build:css -- /assets/terrascale.css

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
COPY --from=css /assets/terrascale.css ./src/TerraScale.Dashboard/wwwroot/css/terrascale.css
RUN dotnet publish src/TerraScale.Dashboard/TerraScale.Dashboard.csproj -c Release -o /app -p:SkipCssBuild=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "TerraScale.Dashboard.dll"]
