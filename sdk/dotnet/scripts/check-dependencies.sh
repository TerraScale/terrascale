#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."

projects=(src/TerraScale.DotNet/TerraScale.DotNet.csproj
    src/TerraScale.EntityFrameworkCore/TerraScale.EntityFrameworkCore.csproj
    scripts/check-nuget-vulnerabilities.cs)
locks=(src/TerraScale.DotNet/packages.lock.json
    src/TerraScale.EntityFrameworkCore/packages.lock.json
    scripts/packages.lock.json)
diff -u <(printf '%s\n' "${locks[@]}" | sort) <(git ls-files '*packages.lock.json' | sort)
for project in "${projects[@]}"; do
    dotnet restore "$project" --locked-mode
done
dotnet run --no-restore --file scripts/check-nuget-vulnerabilities.cs -- "${projects[@]}"
python3 scripts/check-locked-runtime-packages.py
