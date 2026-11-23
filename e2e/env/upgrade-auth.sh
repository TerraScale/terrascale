set -euo pipefail
repo=$(cd "${1:?source root}" && pwd)
build=${2:?output directory}
source=$repo/src/TerraScale.Authentication/bin/Release/net11.0
[ -f "$source/ts-auth.dll" ] || { echo "build ts-auth before staging the second instance" >&2; exit 1; }
compgen -G "$repo/src/TerraScale.Authentication/Storage/Schema/*.sql" > /dev/null || { echo "ts-auth has no DbUp scripts" >&2; exit 1; }
rm -rf "$build"
mkdir -p "$build"
cp -a "$source/." "$build/"
