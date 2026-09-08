# Fresh member placement admission

A fresh member has no local accepted placement history. It must not manufacture
a genesis epoch or install other members' historical proposals as local intent.
The GRU placement issuer can instead attest one current predecessor with a
fresh, member-specific join checkpoint. This keeps join requests bounded as a
placement scope accumulates history.

`PlacementProposalResponse.join_challenge` carries an Ed25519 member-signed
`placement_join_challenge` envelope. Its payload binds the exact scope, member
identity and Region, durable random bootstrap nonce, local proposal revision,
and canonical proposal digest. Stage, Get, and retries return identical bytes
for the same proposal, including after acceptance. The nonce is created with
the journal in one durable transaction. A fresh installation receives a new
nonce even when its configured member URI is reused. A missing or malformed
nonce in an existing nonce table is corruption, not permission to regenerate it.

The issuer verifies the member signature using active enrollment and exact
provider ownership. It checks the current placement scope head and live GRU
authority before signing `placement_join`. The checkpoint binds the challenge
nonce, member identity/Region/enrollment generation/provider resource, prior
signed proof and its digest, successor proof/request digests, local stage
revision/digest, authority epoch/lease/candidate, and issuance/expiry. Both signed
envelopes use the `terrabase-placement-transition-v1\0` signature domain and
separate mandatory `kind` values.

The existing accepted-proof envelope can contain `join_checkpoint`. The entire
acceptance request proof is limited to 256 KiB before JSON parsing or journal
work. Checkpoint first use requires an issuance time no later than the member's
clock, an unexpired grant with lifetime at most 300,000 milliseconds, matching
local nonce/identity/proposal, and all retained authority floors. The recipient
must be absent from the attested predecessor's member set. The staged successor
still undergoes the ordinary database/Region boundary checks. Its old-epoch
fences are verified using the predecessor's authority-admitted receipt keys.

Only an empty acceptance journal can install a checkpoint. The exact checkpoint
bytes and first local acceptance commit in the same SQLite transaction. The
predecessor is an explicit authority attestation, not a fabricated local Stage
or Accept entry. Subsequent membership and drain requests must meet the retained
checkpoint authority epoch and normal lineage rules. A nonempty journal cannot
use a new checkpoint to bypass those rules.

An exact already-accepted epoch/proposal retry returns the retained acceptance.
It does not replace the original checkpoint, even if delivery attaches a renewed
or expired checkpoint. Restart verifies the retained checkpoint and accepted
chain without expiring an already admitted member. These rules authorize
placement acceptance only; encryption, physical replication, catch-up, fencing,
and readiness gates still govern data service.

The independent Go corpus in
`crates/terrabase-tbep/testdata/placement-join-checkpoint.json` verifies a fresh
local join, restart after grant expiry, and a later drain signed by a member
absent from startup receipt pins. Its fixed nonce and injected first-use time
are deterministic test inputs. Separate journal and mTLS tests exercise actual
nonce generation, storage loss, signed challenge delivery, authority rollback,
and atomic failure behavior. These tests do not qualify live OCI provisioning
or the complete production autoscaling journey.
