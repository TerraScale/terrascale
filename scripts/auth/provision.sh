#!/usr/bin/env bash
# Production execution belongs to TS-470. This repository only prints the setup plan.
set -euo pipefail

cat <<'PLAN'
Provisioning plan (no changes executed):
1. Use the Management API to resolve terrascale and organization terrascale and its ts-auth project ID.
2. Provision the auth database and a project/database-scoped tb_ API key with documents:read/write.
3. With the pinned ts-dotnet Documents client, call AuthStore.ProvisionAsync on that existing database.
   It creates uniques, verification_tokens, storage_health, users, sessions, passcodes,
   signing_key_rings (TS-488) and oidc_applications, oidc_authorizations and
   oidc_tokens (TS-491); /readyz requires all of them. Then create the feature
   collection rate_limits (TS-488).
4. Set TS_AUTH_DATABASE=<ts-auth-project-id>/auth and TS_AUTH_TERRASCALE_API_URL to the resolved HTTPS authority.
See docs/parity.md for collection setup, configuration and conditional-write guarantees.
Live execution requires the TS-470 approval flow.
PLAN
