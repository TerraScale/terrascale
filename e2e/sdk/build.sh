set -euo pipefail
sdk=${1:?dotnet, go, rust, python or js}
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
out=${E2E_SDK_BUILD:?E2E_SDK_BUILD is the directory for the built runners}
mkdir -p "$out"
case $sdk in
  dotnet)
    dotnet restore "$here/dotnet/Runner.csproj" --locked-mode
    dotnet build "$here/dotnet/Runner.csproj" --configuration Release --no-restore --output "$out/dotnet"
    ;;
  go)
    (cd "$here/go" && go build -trimpath -o "$out/go/runner" .)
    ;;
  rust)
    (cd "$here/rust" && cargo build --release --locked)
    install -D "${CARGO_TARGET_DIR:-$here/rust/target}/release/terrascale-e2e-runner" "$out/rust/runner"
    ;;
  python)
    python3 -m venv "$out/python"
    "$out/python/bin/pip" install --quiet --disable-pip-version-check "$repo/sdk/python"
    ;;
  js)
    npm ci --prefix "$repo/sdk/js" --omit=dev --no-audit --no-fund
    ;;
  *)
    echo "unknown SDK $sdk" >&2
    exit 2
    ;;
esac
