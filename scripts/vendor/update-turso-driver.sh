#!/usr/bin/env bash
# Re-imports the Turso .NET driver's C# source (tursodatabase/turso, bindings/dotnet/src: Turso.Raw, Turso.Data,
# Turso.Data.Sqlite, with LICENSE.md and NOTICE.md) at <tag> into src/vendor/Turso.Data.Sqlite, then re-applies
# TerraScale's patch series (src/vendor/Turso.Data.Sqlite/patches, in name order). Fails on the first patch that does
# not apply, leaving its conflict markers. See src/vendor/Turso.Data.Sqlite/UPSTREAM.md.
#   scripts/vendor/update-turso-driver.sh <tag>           # working tree only; review with git diff
#   scripts/vendor/update-turso-driver.sh <tag> --commit  # one commit for the pristine import, one for the patches
set -euo pipefail
tag=${1:?usage: scripts/vendor/update-turso-driver.sh <tag> [--commit]}
mode=${2:-}
root=$(git rev-parse --show-toplevel)
vendor=src/vendor/Turso.Data.Sqlite
cd "$root"
if ! git diff --quiet HEAD -- "$vendor" || [[ -n $(git ls-files --others --exclude-standard -- "$vendor") ]]; then
  echo "$vendor has uncommitted changes; commit or discard them first" >&2
  exit 1
fi

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
git clone --quiet --filter=blob:none --no-checkout https://github.com/tursodatabase/turso.git "$work"
git -C "$work" sparse-checkout set --no-cone /LICENSE.md /NOTICE.md \
  /bindings/dotnet/src/Turso.Raw /bindings/dotnet/src/Turso.Data /bindings/dotnet/src/Turso.Data.Sqlite
git -C "$work" -c advice.detachedHead=false checkout --quiet "refs/tags/$tag"
commit=$(git -C "$work" rev-parse HEAD)

rm -rf "$vendor/LICENSE.md" "$vendor/NOTICE.md" "$vendor/src"
mkdir -p "$vendor/src"
cp "$work/LICENSE.md" "$work/NOTICE.md" "$vendor/"
cp -R "$work/bindings/dotnet/src/Turso.Raw" "$work/bindings/dotnet/src/Turso.Data" "$work/bindings/dotnet/src/Turso.Data.Sqlite" "$vendor/src/"
sed -i -e "s|^- Tag: .*|- Tag: \`$tag\`|" -e "s|^- Commit: .*|- Commit: \`$commit\`|" "$vendor/UPSTREAM.md"

if [[ $mode == --commit ]]; then
  git add -A -- "$vendor"
  git commit --quiet -m "vendor: Turso .NET driver $tag ($commit), pristine"
fi

shopt -s nullglob
patches=("$vendor"/patches/*.patch)
for patch in "${patches[@]}"; do
  if ! git apply --3way --whitespace=nowarn --directory="$vendor" "$patch"; then
    echo "$patch does not apply to $tag: resolve the conflicts, then regenerate it (see UPSTREAM.md)" >&2
    exit 1
  fi
done

if [[ $mode == --commit ]]; then
  git add -A -- "$vendor"
  git diff --cached --quiet || git commit --quiet -m "vendor: TerraScale patches on Turso .NET driver $tag"
fi
git status --short -- "$vendor"
echo "Turso .NET driver $tag ($commit) imported into $vendor with ${#patches[@]} patches"
