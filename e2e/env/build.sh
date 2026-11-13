set -euo pipefail
root=$(cd "${1:?source root}" && pwd)
out=${2:?output directory}
service=${3:?terrabase, ts-auth or dashboard}
revision=${E2E_REVISION:?E2E_REVISION is the commit the services are built from}
mkdir -p "$out"
out=$(cd "$out" && pwd)
cd "$root"
case $service in
  terrabase)
    make turso-native
    dotnet restore src/TerraBase/TerraBase.csproj --locked-mode
    dotnet build src/TerraBase/TerraBase.csproj --configuration Release --no-restore -p:SourceRevisionId="$revision"
    rm -rf "$out/terrabase"
    cp -a src/TerraBase/bin/Release/net11.0 "$out/terrabase"
    python3 e2e/fault/build-receipts.py "$root" "$out"
    python3 e2e/usage/build.py "$root" "$out/usage"
    ;;
  ts-auth)
    dotnet restore src/TerraScale.Authentication/TerraScale.Authentication.csproj --locked-mode
    dotnet build src/TerraScale.Authentication/TerraScale.Authentication.csproj --configuration Release --no-restore -p:SourceRevisionId="$revision"
    rm -rf "$out/ts-auth"
    cp -a src/TerraScale.Authentication/bin/Release/net11.0 "$out/ts-auth"
    bash e2e/env/upgrade-auth.sh "$root" "$out/ts-auth-upgrade"
    dotnet restore e2e/social/Journey.Social.csproj --locked-mode
    dotnet build e2e/social/Journey.Social.csproj --configuration Release --no-restore --output "$out/social" -p:SourceRevisionId="$revision"
    ;;
  dashboard)
    dotnet restore src/TerraScale.Dashboard/TerraScale.Dashboard.csproj --locked-mode
    dotnet publish src/TerraScale.Dashboard --configuration Release --no-restore --output "$out/dashboard" -p:SourceRevisionId="$revision"
    dotnet publish e2e/billing/TerraScale.Dashboard.BillingJourney.csproj --configuration Release --output "$out/billing" -p:SourceRevisionId="$revision"
    ;;
  *)
    echo "unknown service $service" >&2
    exit 2
    ;;
esac
