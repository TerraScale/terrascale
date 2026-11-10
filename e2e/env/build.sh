set -euo pipefail
root=$(cd "${1:?source root}" && pwd)
out=${2:?output directory}
revision=${E2E_REVISION:?E2E_REVISION is the commit the services are built from}
mkdir -p "$out"
out=$(cd "$out" && pwd)
cd "$root"
make turso-native
dotnet restore e2e/env/services.slnf --locked-mode
dotnet build e2e/env/services.slnf --configuration Release --no-restore -p:SourceRevisionId="$revision"
dotnet publish src/TerraScale.Dashboard --configuration Release --no-build --output "$out/dashboard"
rm -rf "$out/terrabase" "$out/ts-auth"
cp -a src/TerraBase/bin/Release/net11.0 "$out/terrabase"
cp -a src/TerraScale.Authentication/bin/Release/net11.0 "$out/ts-auth"
