set -euo pipefail
root=$(cd "${1:?source root}" && pwd)
out=${2:?output directory}
service=${3:?terrabase, ts-platform, ts-auth or dashboard}
revision=${E2E_REVISION:?E2E_REVISION is the commit the services are built from}
mkdir -p "$out"
out=$(cd "$out" && pwd)
cd "$root"
published() {
  local directory=$1 library
  shift
  for library in "$@"; do
    [ -f "$directory/runtimes/linux-x64/native/$library" ] || { echo "$directory lacks runtimes/linux-x64/native/$library" >&2; exit 1; }
  done
}
ensure_native() {
  local runtime=$1
  local native_out="native/runtimes/$runtime"
  local inputs
  inputs=$(cat native/native-pin.env native/build-native.sh | sha256sum | cut -d' ' -f1)
  if [ -f "$native_out/native/libsqlite3mc.so" ] \
    && [ -f "$native_out/native/libmsquic.so.2" ] \
    && [ -f "$native_out/native/libnuma.so.1" ] \
    && [ -f "$native_out/inputs.sha256" ] \
    && [ "$(cat "$native_out/inputs.sha256")" = "$inputs" ]; then
    return 0
  fi
  bash native/build-native.sh "$runtime"
  mkdir -p "$native_out"
  printf '%s\n' "$inputs" > "$native_out/inputs.sha256"
}
case $service in
  terrabase)
    ensure_native linux-x64
    dotnet restore src/TerraBase/TerraBase.csproj --locked-mode
    rm -rf "$out/terrabase"
    dotnet publish src/TerraBase/TerraBase.csproj --configuration Release --no-restore \
      -p:PublishSingleFile=false -p:PublishReadyToRun=false -p:SourceRevisionId="$revision" --output "$out/terrabase"
    published "$out/terrabase" libsqlite3mc.so libmsquic.so.2 libnuma.so.1
    dotnet restore e2e/keyring/Journey.Keyring.csproj --locked-mode -p:KeyringServiceDirectory="$out/terrabase"
    dotnet build e2e/keyring/Journey.Keyring.csproj -c Release --no-restore --output "$out/keyring" -p:KeyringServiceDirectory="$out/terrabase" -p:SourceRevisionId="$revision"
    python3 e2e/fault/build-receipts.py "$root" "$out"
    python3 e2e/usage/build.py "$root" "$out/usage"
    ;;
  ts-platform)
    ensure_native linux-x64
    dotnet restore src/TerraScale.Platform/TerraScale.Platform.csproj --locked-mode
    rm -rf "$out/ts-platform"
    dotnet publish src/TerraScale.Platform/TerraScale.Platform.csproj --configuration Release --no-restore \
      -p:PublishSingleFile=false -p:PublishReadyToRun=false -p:SourceRevisionId="$revision" --output "$out/ts-platform"
    published "$out/ts-platform" libmsquic.so.2 libnuma.so.1
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
    dotnet restore e2e/billing/TerraScale.Dashboard.BillingJourney.csproj --locked-mode
    dotnet publish e2e/billing/TerraScale.Dashboard.BillingJourney.csproj --configuration Release --no-restore --output "$out/billing" -p:SourceRevisionId="$revision"
    ;;
  *)
    echo "unknown service $service" >&2
    exit 2
    ;;
esac
