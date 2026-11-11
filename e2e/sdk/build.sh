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
    uv=${E2E_TOOLS:?E2E_TOOLS holds the pinned tools}/uv
    "$uv" venv --quiet --clear --python 3.12 --no-python-downloads "$out/python"
    "$uv" pip sync --quiet --python "$out/python/bin/python" --require-hashes --only-binary :all: "$repo/sdk/python/requirements-ci.txt"
    "$uv" pip install --quiet --python "$out/python/bin/python" --no-deps --no-build-isolation "$repo/sdk/python"
    ;;
  js)
    npm ci --prefix "$repo/sdk/js" --omit=dev --no-audit --no-fund
    ;;
  *)
    echo "unknown SDK $sdk" >&2
    exit 2
    ;;
esac
