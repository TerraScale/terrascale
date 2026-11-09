#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."

# Keep every committed lock in the gate, including file-based build/test tools.
projects=(src/TerraScale.DotNet/TerraScale.DotNet.csproj
    src/TerraScale.EntityFrameworkCore/TerraScale.EntityFrameworkCore.csproj
    tests/TerraScale.DotNet.Tests/TerraScale.DotNet.Tests.csproj
    tests/TerraScale.EntityFrameworkCore.Tests/TerraScale.EntityFrameworkCore.Tests.csproj
    tests/TerraScale.EntityFrameworkCore.Tests.EntityFrameworkDesign/TerraScale.EntityFrameworkCore.Tests.EntityFrameworkDesign.csproj
    scripts/check-nuget-vulnerabilities.cs
    scripts/check-nuget-vulnerabilities.tests.cs)
locks=(src/TerraScale.DotNet/packages.lock.json
    src/TerraScale.EntityFrameworkCore/packages.lock.json
    tests/TerraScale.DotNet.Tests/packages.lock.json
    tests/TerraScale.EntityFrameworkCore.Tests/packages.lock.json
    tests/TerraScale.EntityFrameworkCore.Tests.EntityFrameworkDesign/packages.lock.json
    scripts/packages.lock.json
    scripts/check-nuget-vulnerabilities.tests.packages.lock.json)
diff -u <(printf '%s\n' "${locks[@]}" | sort) <(git ls-files '*packages.lock.json' | sort)
for project in "${projects[@]}"; do
    dotnet restore "$project" --locked-mode
done
dotnet run --no-restore --file scripts/check-nuget-vulnerabilities.cs -- "${projects[@]}"
python3 scripts/check-locked-runtime-packages.py
