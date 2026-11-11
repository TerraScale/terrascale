#!/usr/bin/env bash
set -euo pipefail

bash scripts/check-dependencies.sh
dotnet build TerraScale.DotNet.slnx --configuration Release -p:RestoreLockedMode=true
# The Release build above already fails on analyzer warnings; whitespace is the part of dotnet format it does not cover
# (the full dotnet format re-ran the analyzers: 38 of the job's 96 seconds).
dotnet format whitespace TerraScale.DotNet.slnx --verify-no-changes
