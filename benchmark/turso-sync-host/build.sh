#!/usr/bin/env bash
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
. "$here/turso-pin.env"
out=$here/.build
src=$out/turso-$TURSO_COMMIT
archive=$out/turso-$TURSO_COMMIT.tar.gz
mkdir -p "$out/bin"
if [ ! -f "$archive" ]; then
  curl -fsSL -o "$archive.part" "https://codeload.github.com/tursodatabase/turso/tar.gz/$TURSO_COMMIT"
  mv "$archive.part" "$archive"
fi
echo "$TURSO_SHA256  $archive" | sha256sum -c --quiet -
if [ ! -d "$src" ]; then
  tar -xzf "$archive" -C "$out"
  cp "$src/Cargo.lock" "$out/upstream-Cargo.lock"
  sed -i 's/^members = \[/members = [\n    "ts-sync-revision-host",/' "$src/Cargo.toml"
fi
rm -rf "$src/ts-sync-revision-host"
cp -r "$here/host" "$src/ts-sync-revision-host"
toolchain=${RUSTUP_HOME:-$HOME/.rustup}/toolchains/$RUST_TOOLCHAIN-$(uname -m)-unknown-linux-gnu
[ -x "$toolchain/bin/cargo" ] || rustup toolchain install "$RUST_TOOLCHAIN" --profile minimal >/dev/null
(
  cd "$src"
  PATH=$toolchain/bin:$PATH RUSTC=$toolchain/bin/rustc RUSTFLAGS='--cfg=tokio_unstable' CARGO_INCREMENTAL=0 \
    "$toolchain/bin/cargo" build --profile lib-release -p ts_sync_revision_host -p turso_cli
)
python3 - "$out/upstream-Cargo.lock" "$src/Cargo.lock" <<'PY'
import sys
def packages(path):
    blocks = open(path).read().split("\n[[package]]\n")
    return blocks[0], set(blocks[1:])
head_a, a = packages(sys.argv[1])
head_b, b = packages(sys.argv[2])
added = b - a
if head_a != head_b or a - b or len(added) != 1 or 'name = "ts_sync_revision_host"' not in next(iter(added)):
    sys.exit("Cargo.lock changed beyond the added ts_sync_revision_host package")
PY
cp "$src/target/lib-release/ts-sync-revision-host" "$src/target/lib-release/tursodb" "$out/bin/"
(cd "$out/bin" && sha256sum ts-sync-revision-host tursodb)
