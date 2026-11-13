set -euo pipefail
repo=$1
build=$2
revision=f0fa77892f4260f0da5240dbd9172b0c3185c9c4
fingerprint="$revision:$(sha256sum "$0" | cut -d" " -f1)"
if [ -f "$build/revision" ] && [ "$(cat "$build/revision")" = "$fingerprint" ]; then
  exit 0
fi
git -C "$repo" cat-file -e "$revision" 2>/dev/null || git -C "$repo" fetch --no-tags https://github.com/TerraScale/terrascale.git "$revision"
source=$(mktemp -d)
trap 'rm -rf "$source"' EXIT
git -C "$repo" archive "$revision" | tar -x -C "$source"
python3 - "$source/src/TerraScale.Authentication/Storage/AuthDbContext.Platform.cs" <<'PYBASE'
from pathlib import Path
import sys
path = Path(sys.argv[1])
source = path.read_text()
needle = '            settings.Property(row => row.Providers)'
assert source.count(needle) == 1
source = source.replace(needle, '            settings.Property(row => row.SignUp).HasConversion(value => value ? "public" : "restricted", value => value == "public");\n' + needle)
path.write_text(source)
path = path.with_name('AuthDbContext.cs')
source = path.read_text()
needle = 'property.SetColumnName(Snake(property.Name));'
assert source.count(needle) == 1
source = source.replace(needle, 'property.SetColumnName(property.Name == "SignUp" ? "sign_up_mode" : Snake(property.Name));')
path.write_text(source)
PYBASE
dotnet restore "$source/src/TerraScale.Authentication/TerraScale.Authentication.csproj" --locked-mode
dotnet build "$source/src/TerraScale.Authentication/TerraScale.Authentication.csproj" -c Release --no-restore -p:SourceRevisionId="$revision"
mkdir -p "$build"
cp -a "$source/src/TerraScale.Authentication/bin/Release/net11.0/." "$build/"
printf '%s\n' "$fingerprint" > "$build/revision"
