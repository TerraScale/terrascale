export PATH="$HOME/.local/bin:$HOME/.local/share/mise/shims:$PATH";
set -euo pipefail
: "${E2E_BUILD:?candidate services required}"
revision=06e90caae72043b0ce429a99cb20340317bf77c1
source=$(mktemp -d)
trap 'rm -rf "$source"' EXIT
git cat-file -e "$revision" 2>/dev/null || git fetch --no-tags origin "$revision"
git archive "$revision" | tar -x -C "$source"
mkdir -p "$source/src/vendor/Turso.Data.Sqlite/native/runtimes/linux-x64/native"
cp "$E2E_BUILD/terrabase/"*.so "$source/src/vendor/Turso.Data.Sqlite/native/runtimes/linux-x64/native/"
dotnet restore "$source/src/TerraBase/TerraBase.csproj" --locked-mode
dotnet build "$source/src/TerraBase/TerraBase.csproj" -c Release --no-restore -p:SourceRevisionId="$revision"
mkdir -p "$E2E_BUILD/keyring-baseline"
cp -a "$source/src/TerraBase/bin/Release/net11.0/." "$E2E_BUILD/keyring-baseline/"
cp "$E2E_BUILD/terrabase/"*.so "$E2E_BUILD/keyring-baseline/"
