# Database encryption at rest

Every database, including `_system.db`, uses Turso's `aegis256` page cipher with
a randomly generated 256-bit data key. Database readers, writers and copy
targets must all open with the same cipher and the correct file's key. This
also encrypts WAL pages. Encryption has no activation setting or plaintext
fallback.

## Key custody

Each database has an adjacent `.key` file containing a versioned, wrapped data
key, never the plaintext key. The existing `IMasterKey` boundary generates and
unwraps data keys using AES-256-GCM under the backup master key. A missing,
malformed or unauthentic wrapped key fails closed; an existing plaintext
database is not converted automatically.

Production reuses ts-orch's existing OCI Vault delivery. On the OCI host,
ts-orch authenticates with its instance principal and delivers
`gru-1-terrabase-master-key` through `TERRABASE_MASTER_KEY_FILE` on its private
runtime tmpfs mount. TerraBase does not create a credential, request broader
IAM permissions, or copy a key into its data directory. Test hosts inject a
test-only master-key implementation; production has no test-key fallback.

Database key custody is registered independently of continuous backup.
Development can supply only `TERRABASE_MASTER_KEY_FILE` without configuring
an object-store bucket. Starting storage without a master or a test fake
fails with an explicit configuration error.

Database keys are supplied to the native driver in process memory. Managed
connection strings containing those keys must never be logged. Errors name
the failed operation without echoing key material. This protects persistent
database and backup files; it does not protect a process whose memory or host
identity is compromised.

## Backups and recovery

Encrypted snapshot bundles carry the wrapped key with the encrypted database
inside the existing authenticated backup envelope. A restore does not depend
on a key sidecar remaining on the source node. Continuous backup objects keep
their existing project-key envelope encryption. Snapshot, restore and copy
scratch files must preserve encryption and remove their key sidecars when
the owning operation cleans them up.

Continuous backup's existing `.frames` scratch streams contain decoded
logical changes before upload or during replay. They are not database page
files and still require node-volume encryption. Uploaded log objects remain
inside the authenticated backup envelope. A scan of database and WAL files
does not prove the absence of plaintext in those logical-log scratch streams.

Keep the database and its wrapped-key file together for any physical copy.
Losing the wrapped key makes the corresponding database unreadable even if
the master key survives. Losing the master key makes both wrapped database
keys and existing backup keys unreadable.

## Rotation path

Data-key rotation uses an offline logical copy into a newly created encrypted
database, which receives a fresh data key. Verify the copied content and its
backup before retiring the original file and wrapped key. Coordinate that
cutover with the deployment owner; do not replace the key sidecar on an
existing encrypted database, whose pages still require the original key.

There is no in-place rekey endpoint. Changing the master key's Vault version
alone is not a rotation procedure: existing wrapped keys still require the
old master. Master-key rotation requires separately reviewed offline
rewrapping of every retained database and backup key, with the old master
kept available until retained backups no longer need it. Credential changes
and that operational procedure require their own authorization.

## Format cutover and evidence

Needs format bump: this change advances `StorageFormat.Version` from 12 to 13
for the deployment owner's authorized batch 4 cutover. The deployment owner
wipes and re-bootstraps the serving data after merge; this PR does not perform
that reset. Existing plaintext data cannot be opened by the new binary.
There is no migration or compatibility shim.

Fast integration coverage writes a recognizable document, checks database
and WAL bytes for that marker, restarts with the same fake master, and
checks rejection of damaged key material. The overhead report is
`benchmark/bench/2026-10-23-encryption.md`; distinguish its driver workload
from production HTTP performance.
