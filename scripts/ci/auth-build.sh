#!/usr/bin/env bash
set -euo pipefail
unset DOTNET_PROCESSOR_COUNT DOTNET_GCHeapCount
scripts/vendor/verify-turso-native.sh src/vendor/Turso.Data.Sqlite/native
make -o restore fmt-check SOLUTION=auth.slnf CONFIGURATION=Release
dotnet build scripts/auth/fast.slnx --configuration Release --no-restore --nologo -m
make -o restore -o auth-build -o auth-restore auth-check CONFIGURATION=Release
