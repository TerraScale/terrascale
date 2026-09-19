#!/usr/bin/env fish
# Provision ts-auth on TerraScale as an end user: project in the TerraScale
# organization, project API key, and the backing database.
#
# Requires: a dashboard session token (terrascale auth login), curl, jq.
# Usage: ./scripts/provision.sh

set -euo pipefail

set -l api https://api.terrascale.tech
set -l org_name TerraScale
set -l project_name ts-auth
set -l database ts-auth
set -l region GRU

set -l token (cat ~/.config/terrascale/config.json | jq -r .token)
if test -z "$token" -o "$token" = null
    echo "no dashboard token; run: terrascale auth login" >&2
    exit 1
end

# 1. Resolve the organization.
set -l org (curl -fsS -H "Authorization: Bearer $token" "$api/api/v1/organizations" \
    | jq -r --arg name "$org_name" '.[] | select(.name == $name) | .id')
if test -z "$org"
    echo "organization $org_name not found" >&2
    exit 1
end

# 2. Create the project (owner principal required; Idempotency-Key guards retries).
set -l project (curl -fsS -X POST \
    -H "Authorization: Bearer $token" \
    -H "Idempotency-Key: ts-auth-provision-(date +%s)" \
    -H "Content-Type: application/json" \
    -d "{\"name\":\"$project_name\",\"regions\":[\"$region\"],\"backupRegion\":\"$region\"}" \
    "$api/api/v1/organizations/$org/projects")
set -l project_id (echo $project | jq -r .id)
echo "project: $project_id"

# 3. Create a project API key (returned once — store it).
set -l key (curl -fsS -X POST \
    -H "Authorization: Bearer $token" -H "Content-Type: application/json" -d '{}' \
    "$api/api/v1/projects/$project_id/auth/api-keys")
echo "api key: "(echo $key | jq -r .key)

# 4. Create the database on the regional data plane with that key.
#    (Data-plane routes live on {region}.api.terrascale.tech.)
echo "create database $database with:"
echo "  curl -X POST https://$region.api.terrascale.tech/api/v1/databases \\"
echo "       -H 'Authorization: Bearer <project api key>' \\"
echo "       -H 'X-TerraScale-Project-Id: $project_id' \\"
echo "       -H 'Content-Type: application/json' \\"
echo "       -d '{\"name\":\"$database\",\"placement\":{\"regions\":[\"$region\"]}}'"
