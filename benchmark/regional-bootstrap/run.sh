#!/usr/bin/env bash
# Run inside this monorepo on a ts-heavy-any dev host. No production access.
set -euo pipefail
legacy_revision=b443b6ea83f519018c0d9f9d8f04e65c3df12724
legacy_root="$PWD/.cos/legacy-b443"
mkdir -p "$legacy_root"
git archive "$legacy_revision" | tar -x -C "$legacy_root"
(
  cd "$legacy_root"
  make build
  TERRABASE_REVISION="$legacy_revision" dotnet build src/TerraBase --configuration Debug --no-restore
)
legacy_binary="$legacy_root/src/TerraBase/bin/Debug/net11.0/terrabase.dll"
printf '%s\n' "$legacy_revision" > "$legacy_binary.revision"
export TERRABASE_TEST_LEGACY_BINARY="$legacy_binary"
sha256sum "$legacy_binary"
for zdt_repeat in 1 2 3 4 5; do
  echo "ZDT_BOOTSTRAP_REPEAT=$zdt_repeat"
  dotnet run --project tests/TerraBase.Slow --configuration Debug --no-build -- --treenode-filter '/*/*/*Regional*/*' --output detailed --no-ansi
done
make terrabase-test-fast
