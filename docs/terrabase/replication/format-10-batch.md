# Format-10 A6 batch

Format `10-8e53f392820a` combines durable replica fault recovery (#272) and
source-pinned refill certification (#288). SYSTEM stores node-local copy faults
and write intents; client copies store refill certificates. See
[replica rebuild](../replica-rebuild.md) for recovery and refusal behavior.

This format refuses format-9 directories. There is no migration. The approved
production reset discards application data, including auth identities, sessions,
signing keys and dashboard data, with volume snapshots retained for recovery.
Fly secrets, including the auth master key, remain unchanged.

Use the coordinated reset procedure in [CI and releases](../ci.md): wait for the
complete main workflow's format refusal, hold main merges and retries, fence all
six machines, stage every old TerraBase machine on the target image, and reset
gru, sjc and fra once each. Verify pinned readiness and backup health before
advancing. Retry that same workflow only after all reset regions are healthy
and the CI credential is reseeded. Normal auth deployment must restore routes
and startup policy, reseed the platform identity and replace signing keys.

Acceptance requires all six machines on the merged revision, local ts-auth and
ts-dashboard databases in every region, the TerraScale owner/org/projects,
unchanged Fly secret fingerprints, main E2E and regional email sign-in.
