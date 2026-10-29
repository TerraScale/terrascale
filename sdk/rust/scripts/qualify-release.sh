#!/usr/bin/env bash
set -euo pipefail

[ "$#" -eq 1 ] || { echo "usage: qualify-release.sh <external-output-directory>" >&2; exit 2; }
cd "$(dirname "${BASH_SOURCE[0]}")/.."
repository=$(git rev-parse --show-toplevel)
output=$(realpath -m "$1")
case "$output/" in "$repository/"*) echo 'Release output must be outside the checkout' >&2; exit 2 ;; esac
mkdir -p "$output"
[ -z "$(find "$output" -mindepth 1 -maxdepth 1 -print -quit)" ] || { echo 'Release output must be empty' >&2; exit 2; }

rustup show
test "$(uname -sm)" = 'Linux x86_64'
workspace=$(mktemp -d)
trap 'rm -rf "$workspace"' EXIT
curl --fail --location --silent --show-error \
  https://github.com/rustsec/rustsec/releases/download/cargo-audit/v0.22.2/cargo-audit-x86_64-unknown-linux-musl-v0.22.2.tgz \
  --output "$workspace/cargo-audit.tgz"
printf '%s  %s\n' 7fb9497f8594b389e5fce5ef9b92db08432996895b2e0c5a0167a69ed445c428 "$workspace/cargo-audit.tgz" | sha256sum --check
tar -xOf "$workspace/cargo-audit.tgz" cargo-audit-x86_64-unknown-linux-musl-v0.22.2/cargo-audit > "$workspace/cargo-audit"
chmod +x "$workspace/cargo-audit"
export PATH="$workspace:$PATH"
test -s Cargo.lock
git ls-files -z -- Cargo.lock '**/Cargo.lock' > "$workspace/lockfiles"
test -s "$workspace/lockfiles"
while IFS= read -r -d '' lock; do
  test -s "$lock"
  cargo audit --deny warnings --file "$lock"
done < "$workspace/lockfiles"
cargo fmt --all -- --check
cargo test --locked --all-features
cargo clippy --locked --all-targets --all-features -- -D warnings
cargo publish --dry-run --locked --all-features --registry crates-io
package=$(python3 - <<'PYTHON'
import tomllib
from pathlib import Path
manifest = tomllib.loads(Path("Cargo.toml").read_text())["package"]
print(f"{manifest['name']}-{manifest['version']}")
PYTHON
)
target=$(cargo metadata --no-deps --format-version 1 | python3 -c 'import json,sys; print(json.load(sys.stdin)["target_directory"])')
cargo audit --deny warnings --file "$target/package/$package/Cargo.lock"
cp "$target/package/$package.crate" "$output/"
