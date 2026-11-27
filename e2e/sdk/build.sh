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
    failed=0
    (cd "$here/go" && go build -trimpath -o "$out/go/runner" .) &
    go_runner=$!
    (cd "$repo/sdk/go" && go build -trimpath -o "$out/go/ts" ./cmd/ts) &
    go_ts=$!
    wait "$go_runner" || failed=1
    wait "$go_ts" || failed=1
    [ "$failed" -eq 0 ]
    ;;
  rust)
    (cd "$here/rust" && cargo build --release --locked)
    install -D "${CARGO_TARGET_DIR:-$here/rust/target}/release/terrascale-e2e-runner" "$out/rust/runner"
    ;;
  python)
    uv=${E2E_TOOLS:?E2E_TOOLS holds the pinned tools}/uv
    py_deps=$(sha256sum "$repo/sdk/python/requirements-ci.txt" | cut -d' ' -f1)
    py_src=$(find "$repo/sdk/python/src" "$repo/sdk/python/pyproject.toml" -type f ! -path '*/__pycache__/*' ! -name '*.pyc' -print0 | LC_ALL=C sort -z | xargs -0 sha256sum | sha256sum | cut -d' ' -f1)
    if [ ! -x "$out/python/bin/python" ] \
      || [ ! -f "$out/python/.e2e-deps.sha256" ] \
      || [ "$(cat "$out/python/.e2e-deps.sha256")" != "$py_deps" ]; then
      "$uv" venv --quiet --clear --python 3.12 --no-python-downloads "$out/python"
      "$uv" pip sync --quiet --python "$out/python/bin/python" --require-hashes --only-binary :all: "$repo/sdk/python/requirements-ci.txt"
      printf '%s\n' "$py_deps" > "$out/python/.e2e-deps.sha256"
      rm -f "$out/python/.e2e-src.sha256"
    fi
    if [ ! -f "$out/python/.e2e-src.sha256" ] \
      || [ "$(cat "$out/python/.e2e-src.sha256")" != "$py_src" ]; then
      "$uv" pip install --quiet --python "$out/python/bin/python" --no-deps --no-build-isolation "$repo/sdk/python"
      printf '%s\n' "$py_src" > "$out/python/.e2e-src.sha256"
    fi
    ;;
  js)
    js_lock=$(sha256sum "$repo/sdk/js/package-lock.json" | cut -d' ' -f1)
    if [ ! -d "$repo/sdk/js/node_modules" ] \
      || [ ! -f "$repo/sdk/js/node_modules/.e2e-lock.sha256" ] \
      || [ "$(cat "$repo/sdk/js/node_modules/.e2e-lock.sha256")" != "$js_lock" ]; then
      npm ci --prefix "$repo/sdk/js" --omit=dev --no-audit --no-fund
      printf '%s\n' "$js_lock" > "$repo/sdk/js/node_modules/.e2e-lock.sha256"
    fi
    ;;
  *)
    echo "unknown SDK $sdk" >&2
    exit 2
    ;;
esac
