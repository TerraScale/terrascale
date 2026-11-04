#!/usr/bin/env bash
set -euo pipefail
unset DOTNET_PROCESSOR_COUNT DOTNET_GCHeapCount
scripts/vendor/verify-turso-native.sh src/vendor/Turso.Data.Sqlite/native
# Fast restores its smaller graph; formatting also needs the Slow project assets.
make auth-restore
make -o restore fmt-check SOLUTION=auth.slnf CONFIGURATION=Release
dotnet build tests/TerraScale.Authentication.Fast/TerraScale.Authentication.Fast.csproj --configuration Release --no-restore --nologo
dotnet build src/TerraBase/TerraBase.csproj --configuration Release --no-restore --nologo
make -o restore -o auth-build auth-check CONFIGURATION=Release
