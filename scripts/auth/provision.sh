#!/usr/bin/env bash
# Production execution belongs to TS-470. This repository only prints the setup plan.
set -euo pipefail

cat <<'PLAN'
Provisioning plan (no changes executed):
1. ts-auth reaches TerraBase at http://top1.nearest.of.ts-terrabase.internal:8081, the internal
   listener on Fly's private network; it needs no API key (.cos/fly-migration-plan.md).
2. Resolve organization terrascale and its ts-auth project ID, then create the auth database
   there (PUT /v1/projects/<ts-auth-project-id>/databases/auth).
3. With the pinned ts-dotnet Documents client, call AuthStore.ProvisionAsync on that existing database.
   It creates uniques, verification_tokens, storage_health, users, sessions, passcodes,
   signing_keys, oidc_applications, oidc_authorizations and
   oidc_tokens (TS-491), rate_limits (TS-488, required since TS-582) and
   email_configs and email_config_versions (TS-583), and organizations,
   memberships, projects, project_members, api_keys, invitations and audit_events (TS-598),
   and mfa_credentials (TS-707); /readyz requires all of them.
4. Set TS_AUTH_DATABASE=<ts-auth-project-id>/auth.
See docs/auth/parity.md for collection setup, configuration and conditional-write guarantees.
Live execution requires the TS-470 approval flow.
PLAN
