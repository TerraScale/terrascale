#!/usr/bin/env bash
# Provision the one platform auth database in an existing platform project.
# The operator creates the project and API key first, then supplies their ids
# through TS_AUTH_TERRASCALE_PROJECT_ID and TS_AUTH_TERRASCALE_API_KEY.
# Every launch region gets a replica; GRU is the write authority.
# Requires curl and jq.
set -euo pipefail

api=https://gru.terrascale.tech
database=ts-auth
regions='["GRU","SFO","FRA"]'
residency=GRU,SFO,FRA

: "${TS_AUTH_TERRASCALE_PROJECT_ID:?Set the platform auth project id}"
: "${TS_AUTH_TERRASCALE_API_KEY:?Set the platform auth project API key}"

database_body=$(jq -nc --arg name "$database" --argjson placement "$regions" \
    --arg residency "$residency" \
    '{name: $name, placement: $placement, residency: $residency, operation_id: "ts-auth-global-database-v1"}')
database_result=$(curl -fsS -X POST \
    -H "Authorization: Bearer $TS_AUTH_TERRASCALE_API_KEY" \
    -H "X-TerraScale-Project-Id: $TS_AUTH_TERRASCALE_PROJECT_ID" \
    -H 'Idempotency-Key: ts-auth-global-database-v1' \
    -H 'Content-Type: application/json' \
    --data "$database_body" "$api/api/v1/databases")

printf 'database operation: %s\n' "$database_result"
