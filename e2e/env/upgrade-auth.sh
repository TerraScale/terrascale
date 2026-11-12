set -euo pipefail
repo=$1
build=$2
revision=f0fa77892f4260f0da5240dbd9172b0c3185c9c4
if [ -f "$build/revision" ] && [ "$(cat "$build/revision")" = "$revision" ]; then
  exit 0
fi
git -C "$repo" cat-file -e "$revision" 2>/dev/null || git -C "$repo" fetch --no-tags https://github.com/TerraScale/terrascale.git "$revision"
source=$(mktemp -d)
trap 'rm -rf "$source"' EXIT
git -C "$repo" archive "$revision" | tar -x -C "$source"
dotnet restore "$source/src/TerraScale.Authentication/TerraScale.Authentication.csproj" --locked-mode
dotnet build "$source/src/TerraScale.Authentication/TerraScale.Authentication.csproj" -c Release --no-restore -p:SourceRevisionId="$revision"
mkdir -p "$build"
cp -a "$source/src/TerraScale.Authentication/bin/Release/net11.0/." "$build/"
printf '%s\n' "$revision" > "$build/revision"
