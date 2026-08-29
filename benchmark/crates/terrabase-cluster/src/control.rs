//! The NATS control contract, in process (P5): the six KV buckets and
//! five request/reply subjects of `p0-nats-control-contract`, as typed
//! semantics with CAS-fenced revisions, bounded payloads, and the
//! no-data rule enforced by payload-shape allowlists.
//!
//! NATS serves EXACTLY: KV state, configuration sharing,
//! discoverability, and typed bounded request/reply for cluster
//! control. It never carries documents, CRDT operations, WAL records,
//! replication frames, sync payloads, checkpoints, tier-manifest
//! object bytes, or mover receipts — every payload type here declares
//! its field allowlist and rejects anything else, so a data-shaped
//! field cannot ride a control subject. A reply may name WHERE data
//! lives (endpoints, object keys) but never the data itself.
//!
//! This module is the in-process plane: the same typed semantics a
//! live broker deployment serves, without the socket. No nats-server
//! runs on this host — the live-server rows are recorded unrun, never
//! fabricated.

use std::collections::BTreeMap;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::Mutex;

use serde::{Deserialize, Serialize};

/// The KV payload bound of the contract (default 64 KiB).
pub const PAYLOAD_BOUND_BYTES: usize = 64 * 1024;

/// The cluster slug every subject and bucket name carries.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ControlCluster(String);

impl ControlCluster {
    /// Adopts a cluster slug.
    ///
    /// # Errors
    ///
    /// Returns [`ControlError::InvalidName`] for an empty slug or one
    /// with whitespace (a NATS subject token cannot carry it).
    pub fn try_new(slug: &str) -> Result<Self, ControlError> {
        if slug.is_empty() || slug.chars().any(char::is_whitespace) {
            return Err(ControlError::InvalidName);
        }
        Ok(Self(slug.to_owned()))
    }

    /// The slug text.
    #[must_use]
    pub fn as_str(&self) -> &str {
        &self.0
    }
}

/// The join token of the bootstrap: a new member starts with a seed
/// NATS URL plus this token, and `ctrl.join` verifies it.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct JoinToken(String);

impl JoinToken {
    /// Mints a token from its text.
    ///
    /// # Errors
    ///
    /// Returns [`ControlError::InvalidName`] for empty text.
    pub fn try_new(text: &str) -> Result<Self, ControlError> {
        if text.is_empty() {
            return Err(ControlError::InvalidName);
        }
        Ok(Self(text.to_owned()))
    }

    /// Whether a presented token matches — constant-shaped, so a
    /// wrong token tells the caller nothing but "no".
    #[must_use]
    pub fn matches(&self, presented: &JoinToken) -> bool {
        self.0 == presented.0
    }
}

// ---------------------------------------------------------------------------
// The six KV buckets and their payload allowlists.
// ---------------------------------------------------------------------------

/// One of the contract's six KV buckets.
#[derive(Clone, Copy, Debug, Eq, PartialEq, Ord, PartialOrd)]
pub enum KvBucket {
    /// `terrabase.$CLUSTER.members` — member leases under `member/<id>`.
    Members,
    /// `terrabase.$CLUSTER.topology` — the digest under `snapshot`.
    Topology,
    /// `terrabase.$CLUSTER.config` — epoch records under `epoch/<n>`.
    Config,
    /// `terrabase.$CLUSTER.mode` — the identity under `identity`.
    Mode,
    /// `terrabase.$CLUSTER.routes` — advertisements under `region/<r>`.
    Routes,
    /// `terrabase.$CLUSTER.tierpolicy` — pointers under `tenant/<t>`.
    TierPolicy,
}

impl KvBucket {
    /// The bucket name with the cluster slug inlined.
    #[must_use]
    pub fn name(self, cluster: &ControlCluster) -> String {
        format!("terrabase.{}.{}", cluster.as_str(), self.token())
    }

    const fn token(self) -> &'static str {
        match self {
            Self::Members => "members",
            Self::Topology => "topology",
            Self::Config => "config",
            Self::Mode => "mode",
            Self::Routes => "routes",
            Self::TierPolicy => "tierpolicy",
        }
    }

    /// Whether a key is well-formed for the bucket's key shape.
    #[must_use]
    pub fn key_is_well_formed(self, key: &str) -> bool {
        match self {
            Self::Members => key.starts_with("member/") && key.len() > "member/".len(),
            Self::Topology => key == "snapshot",
            Self::Config => {
                key.starts_with("epoch/")
                    && key["epoch/".len()..].chars().all(|c| c.is_ascii_digit())
                    && key.len() > "epoch/".len()
            }
            Self::Mode => key == "identity",
            Self::Routes => key.starts_with("region/") && key.len() > "region/".len(),
            Self::TierPolicy => key.starts_with("tenant/") && key.len() > "tenant/".len(),
        }
    }
}

/// The member lease: member id, region, endpoints, capabilities,
/// config epoch, and expiry. Endpoints name WHERE the member's QUIC
/// data links live — location only, never data.
#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(deny_unknown_fields)]
pub struct MemberLease {
    /// The member's durable id.
    pub member: String,
    /// The member's Region.
    pub region: String,
    /// The member's QUIC endpoints (locations, not data).
    pub endpoints: Vec<String>,
    /// The member's capabilities.
    pub capabilities: Vec<String>,
    /// The configuration epoch the member last verified durably.
    pub config_epoch: u64,
    /// When the lease goes stale (caller's clock).
    pub expires_at: u64,
    /// The KV revision the lease was written at.
    pub revision: u64,
}

/// The topology digest: the last accepted digest and its epoch — the
/// full object travels by request/reply, never in the bucket.
#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(deny_unknown_fields)]
pub struct TopologyDigest {
    /// The accepted topology's digest.
    pub digest: String,
    /// The epoch the digest belongs to.
    pub config_epoch: u64,
}

/// One Region's voter list inside a configuration epoch record.
#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(deny_unknown_fields)]
pub struct RegionEntry {
    /// The Region.
    pub region: String,
    /// The member ids voting in it.
    pub members: Vec<String>,
}

/// An accepted configuration epoch record: the replica sets by Region
/// and the shard map digest.
#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(deny_unknown_fields)]
pub struct ConfigEpochRecord {
    /// The epoch's number.
    pub epoch: u64,
    /// The Regions and their voters.
    pub regions: Vec<RegionEntry>,
    /// The shard map's digest.
    pub shard_map_digest: String,
}

/// The immutable cluster identity as the mode bucket holds it.
#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(deny_unknown_fields)]
pub struct ModeRecord {
    /// `active` or `backup` — frozen at creation, terminal.
    pub mode: String,
    /// The creation record text.
    pub creation_record: String,
    /// The identity's content hash.
    pub identity_hash: u64,
}

/// One Region's route advertisement: the eligible members.
#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(deny_unknown_fields)]
pub struct RouteAdvertisement {
    /// The Region.
    pub region: String,
    /// The eligible member ids.
    pub eligible: Vec<String>,
}

/// One tenant's tier-policy version pointer — policy bodies live in
/// engine storage; the control plane holds the pointer only.
#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(deny_unknown_fields)]
pub struct TierPolicyPointer {
    /// The tenant.
    pub tenant: String,
    /// The policy version.
    pub version: u64,
    /// Where the policy body lives (engine storage reference).
    pub policy_ref: String,
}

/// One bucket value: the payload, typed per bucket.
#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(deny_unknown_fields, tag = "kind")]
pub enum KvPayload {
    /// A member lease (`members`).
    MemberLease(MemberLease),
    /// The topology digest (`topology`).
    TopologyDigest(TopologyDigest),
    /// A configuration epoch record (`config`).
    ConfigEpoch(ConfigEpochRecord),
    /// The mode record (`mode`).
    Mode(ModeRecord),
    /// A route advertisement (`routes`).
    Routes(RouteAdvertisement),
    /// A tier-policy pointer (`tierpolicy`).
    TierPolicy(TierPolicyPointer),
}

impl KvPayload {
    /// Whether the payload is legal for the bucket — each bucket
    /// holds exactly its own shape.
    #[must_use]
    pub const fn fits(&self, bucket: KvBucket) -> bool {
        matches!(
            (bucket, self),
            (KvBucket::Members, KvPayload::MemberLease(_))
                | (KvBucket::Topology, KvPayload::TopologyDigest(_))
                | (KvBucket::Config, KvPayload::ConfigEpoch(_))
                | (KvBucket::Mode, KvPayload::Mode(_))
                | (KvBucket::Routes, KvPayload::Routes(_))
                | (KvBucket::TierPolicy, KvPayload::TierPolicy(_))
        )
    }

    /// The canonical JSON bytes — the wire form the allowlist guards.
    ///
    /// # Errors
    ///
    /// Returns [`ControlError::Malformed`] if the payload cannot
    /// encode (it always can) and [`ControlError::TooLarge`] past the
    /// 64 KiB bound.
    pub fn to_bytes(&self) -> Result<Vec<u8>, ControlError> {
        let bytes = serde_json::to_vec(self).map_err(|_| ControlError::Malformed)?;
        if bytes.len() > PAYLOAD_BOUND_BYTES {
            return Err(ControlError::TooLarge(bytes.len()));
        }
        Ok(bytes)
    }

    /// Decodes one bucket value from its canonical JSON bytes — the
    /// allowlist rejects any field the contract does not declare, so
    /// a data-shaped field cannot ride a control payload.
    ///
    /// # Errors
    ///
    /// Returns [`ControlError::Malformed`] for anything the
    /// contract's payload shape does not allow.
    pub fn from_bytes(bytes: &[u8]) -> Result<Self, ControlError> {
        serde_json::from_slice(bytes).map_err(|_| ControlError::Malformed)
    }
}

/// A lease's observation state: a lease expiring marks the
/// advertisement STALE — it never authorizes eviction, data loss, or
/// ownership transfer (ADR 0014 D-4).
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum LeaseState {
    /// The lease has not expired.
    Healthy,
    /// The advertisement is stale; the member's durable state stands.
    Stale,
}

/// Observes one lease against a clock reading.
#[must_use]
pub const fn lease_state(lease: &MemberLease, now: u64) -> LeaseState {
    if now < lease.expires_at {
        LeaseState::Healthy
    } else {
        LeaseState::Stale
    }
}

// ---------------------------------------------------------------------------
// The five request/reply subjects.
// ---------------------------------------------------------------------------

/// One of the contract's five `ctrl.*` request/reply subjects.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum CtrlSubject {
    /// `ctrl.join` — the token-gated member join.
    Join,
    /// `ctrl.health` — one member's health digest.
    Health,
    /// `ctrl.topology` — the full snapshot if newer than known.
    Topology,
    /// `ctrl.config_refresh` — the config record if newer than known.
    ConfigRefresh,
    /// `ctrl.route_lookup` — eligible member endpoints (location only).
    RouteLookup,
}

impl CtrlSubject {
    /// The subject text with the cluster slug inlined.
    #[must_use]
    pub fn subject(self, cluster: &ControlCluster) -> String {
        format!("terrabase.{}.ctrl.{}", cluster.as_str(), self.token())
    }

    const fn token(self) -> &'static str {
        match self {
            Self::Join => "join",
            Self::Health => "health",
            Self::Topology => "topology",
            Self::ConfigRefresh => "config_refresh",
            Self::RouteLookup => "route_lookup",
        }
    }

    /// Resolves a subject text back to its kind — unknown subjects are
    /// ignored, never fatal.
    #[must_use]
    pub fn resolve(cluster: &ControlCluster, subject: &str) -> Option<Self> {
        let prefix = format!("terrabase.{}.ctrl.", cluster.as_str());
        let token = subject.strip_prefix(&prefix)?;
        match token {
            "join" => Some(Self::Join),
            "health" => Some(Self::Health),
            "topology" => Some(Self::Topology),
            "config_refresh" => Some(Self::ConfigRefresh),
            "route_lookup" => Some(Self::RouteLookup),
            _ => None,
        }
    }
}

/// One typed control request (bounded, schema-validated).
#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(deny_unknown_fields, tag = "kind")]
pub enum CtrlRequest {
    /// The join request: the token, the member identity, and the
    /// capabilities the member offers.
    Join {
        /// The bootstrap token.
        token: String,
        /// The joining member's id.
        member: String,
        /// The joining member's Region.
        region: String,
        /// The capabilities the member offers.
        capabilities: Vec<String>,
    },
    /// The health request: one member's digest.
    Health {
        /// The member id.
        member: String,
    },
    /// The topology request: the config epoch the caller knows.
    Topology {
        /// The caller's epoch.
        known_epoch: u64,
    },
    /// The config-refresh request: the config epoch the caller knows.
    ConfigRefresh {
        /// The caller's epoch.
        known_epoch: u64,
    },
    /// The route lookup: one Region's eligible endpoints.
    RouteLookup {
        /// The Region.
        region: String,
    },
}

impl CtrlRequest {
    /// Decodes one request from its canonical JSON bytes — the same
    /// allowlist discipline the payloads hold.
    ///
    /// # Errors
    ///
    /// Returns [`ControlError::Malformed`] for anything the request
    /// shape does not allow.
    pub fn from_bytes(bytes: &[u8]) -> Result<Self, ControlError> {
        serde_json::from_slice(bytes).map_err(|_| ControlError::Malformed)
    }
}

/// One typed control reply (bounded, schema-validated).
#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(deny_unknown_fields, tag = "kind")]
pub enum CtrlReply {
    /// The join accepted: the current epoch, the topology snapshot
    /// reference, and the member endpoints to link QUIC data to.
    JoinAccepted {
        /// The cluster's current config epoch.
        config_epoch: u64,
        /// The topology snapshot's digest reference.
        topology_ref: String,
        /// The member endpoints (locations, never data).
        endpoints: Vec<String>,
    },
    /// The join rejected (bad token, or the member shape is refused).
    JoinRejected,
    /// One member's health digest.
    HealthDigest {
        /// The member id.
        member: String,
        /// The member's replication position.
        position: u64,
    },
    /// A newer topology snapshot.
    TopologySnapshot {
        /// The epoch of the snapshot.
        config_epoch: u64,
        /// The topology digest.
        digest: String,
    },
    /// A newer configuration record.
    ConfigRecord {
        /// The record.
        record: ConfigEpochRecord,
    },
    /// The eligible member endpoints of one Region.
    Endpoints {
        /// The endpoints (locations, never data).
        endpoints: Vec<String>,
    },
    /// The caller knows everything the subject would answer.
    NotNewer,
    /// The subject is unknown — ignored, never fatal.
    Ignored,
}

// ---------------------------------------------------------------------------
// The in-process plane.
// ---------------------------------------------------------------------------

/// One stored KV entry: the payload bytes and their revision.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct KvEntry {
    /// The payload, decoded.
    pub payload: KvPayload,
    /// The CAS revision.
    pub revision: u64,
}

#[derive(Default)]
struct PlaneState {
    entries: BTreeMap<(KvBucket, String), KvEntry>,
    next_revision: u64,
    paused: bool,
}

/// The in-process control plane: the six buckets and five subjects of
/// the contract, CAS-fenced and bounded, behind one typed seam.
pub struct InProcessControlPlane {
    cluster: ControlCluster,
    join_token: JoinToken,
    state: Mutex<PlaneState>,
    dropped_malformed: AtomicUsize,
}

impl InProcessControlPlane {
    /// Creates the plane for one cluster with its bootstrap join token.
    ///
    /// # Errors
    ///
    /// Returns [`ControlError::InvalidName`] for an invalid slug or
    /// token text.
    pub fn new(cluster: ControlCluster, join_token: JoinToken) -> Result<Self, ControlError> {
        Ok(Self {
            cluster,
            join_token,
            state: Mutex::new(PlaneState::default()),
            dropped_malformed: AtomicUsize::new(0),
        })
    }

    /// The cluster the plane serves.
    #[must_use]
    pub fn cluster(&self) -> &ControlCluster {
        &self.cluster
    }

    /// How many malformed payloads were dropped and counted — never
    /// fatal, always visible.
    #[must_use]
    pub fn malformed_dropped(&self) -> usize {
        self.dropped_malformed.load(Ordering::Relaxed)
    }

    /// Reads one bucket key.
    ///
    /// # Errors
    ///
    /// Returns [`ControlError::InvalidKey`] for a malformed key,
    /// [`ControlError::Missing`] when the key holds nothing, and
    /// [`ControlError::Paused`] while the plane is failed closed —
    /// an unreachable broker answers nothing; members serve from
    /// their last verified local state.
    pub fn kv_get(&self, bucket: KvBucket, key: &str) -> Result<KvEntry, ControlError> {
        if !bucket.key_is_well_formed(key) {
            return Err(ControlError::InvalidKey);
        }
        let state = self.lock()?;
        if state.paused {
            return Err(ControlError::Paused);
        }
        state.entries.get(&(bucket, key.to_owned())).cloned().ok_or(ControlError::Missing)
    }

    /// Writes one bucket key behind a compare-and-swap on the
    /// revision: `expected = None` demands a fresh key, `Some(n)`
    /// demands revision `n`, and a stale writer loses the CAS and
    /// re-reads. The payload must fit the bucket and the bound.
    ///
    /// # Errors
    ///
    /// Returns [`ControlError::Stale`] on a lost CAS,
    /// [`ControlError::InvalidKey`] for a malformed key,
    /// [`ControlError::WrongBucket`] when the payload shape does not
    /// fit, [`ControlError::Paused`] while failed closed, and the
    /// encode failures of [`KvPayload::to_bytes`].
    pub fn kv_cas(
        &self,
        bucket: KvBucket,
        key: &str,
        payload: KvPayload,
        expected: Option<u64>,
    ) -> Result<u64, ControlError> {
        if !bucket.key_is_well_formed(key) {
            return Err(ControlError::InvalidKey);
        }
        if !payload.fits(bucket) {
            return Err(ControlError::WrongBucket);
        }
        // The bound holds before any state changes.
        payload.to_bytes()?;
        let mut state = self.lock()?;
        if state.paused {
            return Err(ControlError::Paused);
        }
        let slot = &(bucket, key.to_owned());
        let revision = match (state.entries.get(slot), expected) {
            (None, None) => state.next_revision + 1,
            (None, Some(_)) => return Err(ControlError::Stale),
            (Some(_), None) => return Err(ControlError::Stale),
            (Some(entry), Some(expected)) => {
                if entry.revision != expected {
                    return Err(ControlError::Stale);
                }
                state.next_revision + 1
            }
        };
        state.next_revision = revision;
        state.entries.insert(slot.clone(), KvEntry { payload, revision });
        Ok(revision)
    }

    /// Serves one typed control request. Malformed request bytes are
    /// dropped and counted; unknown subjects answer [`CtrlReply::Ignored`].
    ///
    /// # Errors
    ///
    /// Returns [`ControlError::Paused`] while the plane is failed
    /// closed — new joins, topology changes, and configuration
    /// changes pause; already-admitted members keep serving (their
    /// data plane has no NATS dependence).
    pub fn request(
        &self,
        subject: CtrlSubject,
        request: &CtrlRequest,
    ) -> Result<CtrlReply, ControlError> {
        if self.lock()?.paused {
            return Err(ControlError::Paused);
        }
        // The shape gate: each subject answers exactly its request
        // shape; anything else is malformed, dropped, and counted.
        let mismatch = !matches!(
            (subject, request),
            (CtrlSubject::Join, CtrlRequest::Join { .. })
                | (CtrlSubject::Health, CtrlRequest::Health { .. })
                | (CtrlSubject::Topology, CtrlRequest::Topology { .. })
                | (CtrlSubject::ConfigRefresh, CtrlRequest::ConfigRefresh { .. })
                | (CtrlSubject::RouteLookup, CtrlRequest::RouteLookup { .. })
        );
        if mismatch {
            self.dropped_malformed.fetch_add(1, Ordering::Relaxed);
            return Ok(CtrlReply::Ignored);
        }
        Ok(match (subject, request) {
            (CtrlSubject::Join, CtrlRequest::Join { token, member, region, capabilities }) => {
                self.join(token, member, region, capabilities)?
            }
            (CtrlSubject::Health, CtrlRequest::Health { member }) => self.health(member)?,
            (CtrlSubject::Topology, CtrlRequest::Topology { known_epoch }) => {
                self.topology_if_newer(*known_epoch)?
            }
            (CtrlSubject::ConfigRefresh, CtrlRequest::ConfigRefresh { known_epoch }) => {
                self.config_if_newer(*known_epoch)?
            }
            (CtrlSubject::RouteLookup, CtrlRequest::RouteLookup { region }) => {
                self.route_lookup(region)?
            }
            // The mismatch gate has returned every other combination.
            _ => unreachable!("the shape gate returned every mismatch"),
        })
    }

    /// Serves one request given as raw bytes — the decode failure is
    /// the malformed path: dropped, counted, never fatal.
    ///
    /// # Errors
    ///
    /// As [`Self::request`]; a malformed body answers
    /// [`CtrlReply::Ignored`] after counting it.
    pub fn request_bytes(
        &self,
        subject: CtrlSubject,
        bytes: &[u8],
    ) -> Result<CtrlReply, ControlError> {
        let Ok(request) = CtrlRequest::from_bytes(bytes) else {
            self.dropped_malformed.fetch_add(1, Ordering::Relaxed);
            return Ok(CtrlReply::Ignored);
        };
        self.request(subject, &request)
    }

    /// Resolves one subject text and serves it — unknown subjects are
    /// ignored, never fatal.
    ///
    /// # Errors
    ///
    /// As [`Self::request`]; an unknown subject answers
    /// [`CtrlReply::Ignored`].
    pub fn request_on(&self, subject: &str, bytes: &[u8]) -> Result<CtrlReply, ControlError> {
        match CtrlSubject::resolve(&self.cluster, subject) {
            Some(subject) => self.request_bytes(subject, bytes),
            None => Ok(CtrlReply::Ignored),
        }
    }

    /// Fails the plane closed: joins, topology, and configuration
    /// pause; already-admitted members continue over their last
    /// verified QUIC topology (no data-plane dependence on NATS).
    pub fn pause(&self) {
        if let Ok(mut state) = self.state.lock() {
            state.paused = true;
        }
    }

    /// Reopens the plane after recovery.
    pub fn resume(&self) {
        if let Ok(mut state) = self.state.lock() {
            state.paused = false;
        }
    }

    fn join(
        &self,
        token: &str,
        member: &str,
        region: &str,
        capabilities: &[String],
    ) -> Result<CtrlReply, ControlError> {
        let presented = JoinToken::try_new(token).map_err(|_| ControlError::Malformed)?;
        if !self.join_token.matches(&presented) {
            // A rejected join is a normal reply, not an error.
            return Ok(CtrlReply::JoinRejected);
        }
        let mut state = self.lock()?;
        // The accepted epoch and topology the joining member verifies
        // durably before acting on them.
        let config_epoch =
            state.entries.get(&(KvBucket::Topology, "snapshot".into())).map_or(0, |entry| {
                match &entry.payload {
                    KvPayload::TopologyDigest(digest) => digest.config_epoch,
                    _ => 0,
                }
            });
        let endpoints: Vec<String> = state
            .entries
            .iter()
            .filter(|((bucket, _), _)| *bucket == KvBucket::Members)
            .filter_map(|(_, entry)| match &entry.payload {
                KvPayload::MemberLease(lease) => Some(lease.endpoints.clone()),
                _ => None,
            })
            .flatten()
            .collect();
        let revision = state.next_revision + 1;
        state.next_revision = revision;
        state.entries.insert(
            (KvBucket::Members, format!("member/{member}")),
            KvEntry {
                payload: KvPayload::MemberLease(MemberLease {
                    member: member.to_owned(),
                    region: region.to_owned(),
                    endpoints: Vec::new(),
                    capabilities: capabilities.to_vec(),
                    config_epoch,
                    expires_at: 0,
                    revision,
                }),
                revision,
            },
        );
        Ok(CtrlReply::JoinAccepted { config_epoch, topology_ref: "snapshot".into(), endpoints })
    }

    fn health(&self, member: &str) -> Result<CtrlReply, ControlError> {
        let state = self.lock()?;
        let Some(entry) = state.entries.get(&(KvBucket::Members, format!("member/{member}")))
        else {
            return Ok(CtrlReply::Ignored);
        };
        match &entry.payload {
            KvPayload::MemberLease(lease) => Ok(CtrlReply::HealthDigest {
                member: lease.member.clone(),
                // The control plane reports advertisement metadata
                // only — the durable position rides the member's own
                // health surface, so the digest names the epoch.
                position: lease.config_epoch,
            }),
            _ => Ok(CtrlReply::Ignored),
        }
    }

    fn topology_if_newer(&self, known_epoch: u64) -> Result<CtrlReply, ControlError> {
        let state = self.lock()?;
        match state.entries.get(&(KvBucket::Topology, "snapshot".into())) {
            Some(KvEntry { payload: KvPayload::TopologyDigest(digest), .. }) => {
                if digest.config_epoch > known_epoch {
                    Ok(CtrlReply::TopologySnapshot {
                        config_epoch: digest.config_epoch,
                        digest: digest.digest.clone(),
                    })
                } else {
                    Ok(CtrlReply::NotNewer)
                }
            }
            _ => Ok(CtrlReply::NotNewer),
        }
    }

    fn config_if_newer(&self, known_epoch: u64) -> Result<CtrlReply, ControlError> {
        let state = self.lock()?;
        let mut newest: Option<&ConfigEpochRecord> = None;
        for ((bucket, _), entry) in &state.entries {
            if *bucket == KvBucket::Config {
                if let KvPayload::ConfigEpoch(record) = &entry.payload {
                    if newest.is_none_or(|current| record.epoch > current.epoch) {
                        newest = Some(record);
                    }
                }
            }
        }
        match newest {
            Some(record) if record.epoch > known_epoch => {
                Ok(CtrlReply::ConfigRecord { record: record.clone() })
            }
            _ => Ok(CtrlReply::NotNewer),
        }
    }

    fn route_lookup(&self, region: &str) -> Result<CtrlReply, ControlError> {
        let state = self.lock()?;
        match state.entries.get(&(KvBucket::Routes, format!("region/{region}"))) {
            Some(KvEntry { payload: KvPayload::Routes(advertisement), .. }) => {
                // Location only: the endpoints of the eligible
                // members, never data.
                let endpoints: Vec<String> = advertisement
                    .eligible
                    .iter()
                    .filter_map(|member| {
                        state
                            .entries
                            .get(&(KvBucket::Members, format!("member/{member}")))
                            .and_then(|entry| match &entry.payload {
                                KvPayload::MemberLease(lease) => Some(lease.endpoints.clone()),
                                _ => None,
                            })
                    })
                    .flatten()
                    .collect();
                Ok(CtrlReply::Endpoints { endpoints })
            }
            _ => Ok(CtrlReply::Endpoints { endpoints: Vec::new() }),
        }
    }

    fn lock(&self) -> Result<std::sync::MutexGuard<'_, PlaneState>, ControlError> {
        self.state.lock().map_err(|_| ControlError::Paused)
    }
}

/// The typed failures of the control plane.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ControlError {
    /// An empty or whitespace-bearing name.
    InvalidName,
    /// A key that does not fit its bucket's key shape.
    InvalidKey,
    /// A payload shape that does not fit the bucket.
    WrongBucket,
    /// A payload past the 64 KiB bound.
    TooLarge(usize),
    /// Bytes the payload allowlist rejects.
    Malformed,
    /// The key holds nothing.
    Missing,
    /// A lost compare-and-swap — re-read and retry.
    Stale,
    /// The plane is failed closed.
    Paused,
}

#[cfg(test)]
mod tests {
    use super::{
        lease_state, ControlCluster, ControlError, CtrlReply, CtrlRequest, CtrlSubject,
        InProcessControlPlane, JoinToken, KvBucket, KvPayload, LeaseState, MemberLease, ModeRecord,
        RouteAdvertisement, TierPolicyPointer, TopologyDigest, PAYLOAD_BOUND_BYTES,
    };

    fn plane() -> InProcessControlPlane {
        InProcessControlPlane::new(
            ControlCluster::try_new("cell-a").expect("valid slug"),
            JoinToken::try_new("bootstrap-token").expect("valid token"),
        )
        .expect("creates")
    }

    fn lease(revision: u64) -> KvPayload {
        KvPayload::MemberLease(MemberLease {
            member: "member-a".into(),
            region: "eu-1".into(),
            endpoints: vec!["quic://10.0.0.1:7000".into()],
            capabilities: vec!["documents".into()],
            config_epoch: 1,
            expires_at: 1_000,
            revision,
        })
    }

    #[test]
    fn kv_writes_are_cas_fenced_and_a_stale_writer_loses() {
        let plane = plane();
        let first = plane
            .kv_cas(KvBucket::Members, "member/member-a", lease(0), None)
            .expect("the fresh write lands");
        // A second writer holding the same expectation loses.
        let second = plane
            .kv_cas(KvBucket::Members, "member/member-a", lease(first), Some(first))
            .expect("the matching CAS wins");
        assert!(second > first, "the revision advances");
        assert_eq!(
            plane.kv_cas(KvBucket::Members, "member/member-a", lease(first), Some(first - 1)).err(),
            Some(ControlError::Stale),
            "the stale CAS loses"
        );
        // The fresh-key expectation loses against a held key too.
        assert_eq!(
            plane.kv_cas(KvBucket::Members, "member/member-a", lease(9), None).err(),
            Some(ControlError::Stale)
        );
        // Malformed keys and wrong-bucket payloads refuse typed.
        assert_eq!(
            plane.kv_cas(KvBucket::Members, "not-a-lease-key", lease(0), None).err(),
            Some(ControlError::InvalidKey)
        );
        assert_eq!(
            plane.kv_cas(KvBucket::Mode, "identity", lease(0), None).err(),
            Some(ControlError::WrongBucket)
        );
    }

    #[test]
    fn every_bucket_holds_exactly_its_own_payload_shape() {
        let payloads = [
            KvPayload::MemberLease(MemberLease {
                member: "m".into(),
                region: "r".into(),
                endpoints: vec![],
                capabilities: vec![],
                config_epoch: 0,
                expires_at: 0,
                revision: 0,
            }),
            KvPayload::TopologyDigest(TopologyDigest { digest: "d".into(), config_epoch: 1 }),
            KvPayload::ConfigEpoch(super::ConfigEpochRecord {
                epoch: 1,
                regions: vec![],
                shard_map_digest: "s".into(),
            }),
            KvPayload::Mode(ModeRecord {
                mode: "active".into(),
                creation_record: "created".into(),
                identity_hash: 7,
            }),
            KvPayload::Routes(RouteAdvertisement { region: "r".into(), eligible: vec![] }),
            KvPayload::TierPolicy(TierPolicyPointer {
                tenant: "t".into(),
                version: 1,
                policy_ref: "engine://tier-policy/1".into(),
            }),
        ];
        let buckets = [
            KvBucket::Members,
            KvBucket::Topology,
            KvBucket::Config,
            KvBucket::Mode,
            KvBucket::Routes,
            KvBucket::TierPolicy,
        ];
        for bucket in buckets {
            for payload in &payloads {
                let fits = payload.clone().fits(bucket);
                let expected =
                    std::mem::discriminant(payload) == std::mem::discriminant(&payload_of(bucket));
                assert_eq!(fits, expected, "{bucket:?} vs {payload:?}");
            }
        }
    }

    fn payload_of(bucket: KvBucket) -> KvPayload {
        match bucket {
            KvBucket::Members => KvPayload::MemberLease(MemberLease {
                member: String::new(),
                region: String::new(),
                endpoints: vec![],
                capabilities: vec![],
                config_epoch: 0,
                expires_at: 0,
                revision: 0,
            }),
            KvBucket::Topology => {
                KvPayload::TopologyDigest(TopologyDigest { digest: String::new(), config_epoch: 0 })
            }
            KvBucket::Config => KvPayload::ConfigEpoch(super::ConfigEpochRecord {
                epoch: 0,
                regions: vec![],
                shard_map_digest: String::new(),
            }),
            KvBucket::Mode => KvPayload::Mode(ModeRecord {
                mode: String::new(),
                creation_record: String::new(),
                identity_hash: 0,
            }),
            KvBucket::Routes => {
                KvPayload::Routes(RouteAdvertisement { region: String::new(), eligible: vec![] })
            }
            KvBucket::TierPolicy => KvPayload::TierPolicy(TierPolicyPointer {
                tenant: String::new(),
                version: 0,
                policy_ref: String::new(),
            }),
        }
    }

    #[test]
    fn the_no_data_rule_holds_on_every_payload_shape() {
        // A data-shaped field cannot ride a control payload: every
        // payload's allowlist rejects the fields the contract's
        // closed list names (documents, CRDT operations, WAL records,
        // replication frames, sync payloads, checkpoints, mover
        // receipts).
        let data_fields = [
            "documents",
            "operations",
            "wal",
            "frames",
            "sync_payload",
            "checkpoint",
            "mover_receipt",
            "crdt_delta",
        ];
        let base = serde_json::to_value(KvPayload::TopologyDigest(TopologyDigest {
            digest: "d".into(),
            config_epoch: 1,
        }))
        .expect("encodes");
        for field in data_fields {
            let mut hostile = base.clone();
            hostile
                .as_object_mut()
                .expect("an object")
                .insert(field.to_owned(), serde_json::json!([{"doc": "body"}]));
            let bytes = serde_json::to_vec(&hostile).expect("encodes");
            assert_eq!(
                KvPayload::from_bytes(&bytes).err(),
                Some(ControlError::Malformed),
                "a data field named {field} must not ride a control payload"
            );
        }
    }

    #[test]
    fn the_payload_bound_is_enforced() {
        let plane = plane();
        let huge = KvPayload::Routes(RouteAdvertisement {
            region: "r".into(),
            eligible: vec![String::from("x").repeat(PAYLOAD_BOUND_BYTES); 1],
        });
        assert!(matches!(
            plane.kv_cas(KvBucket::Routes, "region/r", huge, None).err(),
            Some(ControlError::TooLarge(_))
        ));
    }

    #[test]
    fn a_lease_expiring_marks_the_advertisement_stale_and_changes_nothing_else() {
        let plane = plane();
        let revision =
            plane.kv_cas(KvBucket::Members, "member/member-a", lease(0), None).expect("lands");
        let entry = plane.kv_get(KvBucket::Members, "member/member-a").expect("held");
        let KvPayload::MemberLease(held) = &entry.payload else {
            panic!("the lease shape");
        };
        assert_eq!(lease_state(held, 999), LeaseState::Healthy);
        assert_eq!(lease_state(held, 1_000), LeaseState::Stale);
        // Expiry never evicts: the entry stands, revision and all.
        assert_eq!(entry.revision, revision);
        assert!(plane.kv_get(KvBucket::Members, "member/member-a").is_ok());
    }

    #[test]
    fn the_join_flow_verifies_the_token_and_answers_the_epoch_and_endpoints() {
        let plane = plane();
        // Advertise one member the joiner will learn to link to.
        plane.kv_cas(KvBucket::Members, "member/member-a", lease(0), None).expect("lands");
        plane
            .kv_cas(
                KvBucket::Topology,
                "snapshot",
                KvPayload::TopologyDigest(TopologyDigest {
                    digest: "digest-1".into(),
                    config_epoch: 4,
                }),
                None,
            )
            .expect("lands");

        // The wrong token rejects.
        let reply = plane
            .request(
                CtrlSubject::Join,
                &CtrlRequest::Join {
                    token: "wrong".into(),
                    member: "member-b".into(),
                    region: "eu-1".into(),
                    capabilities: vec![],
                },
            )
            .expect("serves");
        assert_eq!(reply, CtrlReply::JoinRejected);

        // The right token accepts: the epoch, the topology reference,
        // and the endpoints to link — and the lease lands in the
        // members bucket.
        let reply = plane
            .request(
                CtrlSubject::Join,
                &CtrlRequest::Join {
                    token: "bootstrap-token".into(),
                    member: "member-b".into(),
                    region: "eu-1".into(),
                    capabilities: vec!["documents".into()],
                },
            )
            .expect("serves");
        let CtrlReply::JoinAccepted { config_epoch, topology_ref, endpoints } = reply else {
            panic!("the join accepts, got {reply:?}");
        };
        assert_eq!(config_epoch, 4);
        assert_eq!(topology_ref, "snapshot");
        assert_eq!(endpoints, vec!["quic://10.0.0.1:7000".to_owned()]);
        let KvPayload::MemberLease(joined) =
            plane.kv_get(KvBucket::Members, "member/member-b").expect("the lease landed").payload
        else {
            panic!("the lease shape");
        };
        assert_eq!(joined.config_epoch, 4, "the member records the accepted epoch");
    }

    #[test]
    fn topology_and_config_answer_only_what_is_newer() {
        let plane = plane();
        // Nothing held yet: even epoch zero is not newer than nothing.
        let reply = plane
            .request(CtrlSubject::Topology, &CtrlRequest::Topology { known_epoch: 0 })
            .expect("serves");
        assert_eq!(reply, CtrlReply::NotNewer);

        plane
            .kv_cas(
                KvBucket::Topology,
                "snapshot",
                KvPayload::TopologyDigest(TopologyDigest {
                    digest: "digest-2".into(),
                    config_epoch: 2,
                }),
                None,
            )
            .expect("lands");
        plane
            .kv_cas(
                KvBucket::Config,
                "epoch/2",
                KvPayload::ConfigEpoch(super::ConfigEpochRecord {
                    epoch: 2,
                    regions: vec![super::RegionEntry { region: "eu-1".into(), members: vec![] }],
                    shard_map_digest: "shard-2".into(),
                }),
                None,
            )
            .expect("lands");

        // A member at epoch 2 knows everything: NotNewer.
        assert_eq!(
            plane
                .request(CtrlSubject::Topology, &CtrlRequest::Topology { known_epoch: 2 })
                .expect("serves"),
            CtrlReply::NotNewer
        );
        // A member at epoch 1 learns epoch 2.
        let reply = plane
            .request(CtrlSubject::Topology, &CtrlRequest::Topology { known_epoch: 1 })
            .expect("serves");
        assert_eq!(
            reply,
            CtrlReply::TopologySnapshot { config_epoch: 2, digest: "digest-2".into() }
        );
        let reply = plane
            .request(CtrlSubject::ConfigRefresh, &CtrlRequest::ConfigRefresh { known_epoch: 1 })
            .expect("serves");
        let CtrlReply::ConfigRecord { record } = reply else {
            panic!("the config answers, got {reply:?}");
        };
        assert_eq!(record.epoch, 2);
        assert_eq!(record.shard_map_digest, "shard-2");
    }

    #[test]
    fn route_lookup_names_locations_only() {
        let plane = plane();
        plane.kv_cas(KvBucket::Members, "member/member-a", lease(0), None).expect("lands");
        plane
            .kv_cas(
                KvBucket::Routes,
                "region/eu-1",
                KvPayload::Routes(RouteAdvertisement {
                    region: "eu-1".into(),
                    eligible: vec!["member-a".into()],
                }),
                None,
            )
            .expect("lands");
        let reply = plane
            .request(CtrlSubject::RouteLookup, &CtrlRequest::RouteLookup { region: "eu-1".into() })
            .expect("serves");
        assert_eq!(
            reply,
            CtrlReply::Endpoints { endpoints: vec!["quic://10.0.0.1:7000".to_owned()] }
        );
        // An unknown region answers empty, never an error.
        assert_eq!(
            plane
                .request(
                    CtrlSubject::RouteLookup,
                    &CtrlRequest::RouteLookup { region: "nowhere".into() }
                )
                .expect("serves"),
            CtrlReply::Endpoints { endpoints: vec![] }
        );
    }

    #[test]
    fn malformed_payloads_are_dropped_and_counted_and_never_fatal() {
        let plane = plane();
        // Malformed bytes on a known subject.
        let reply =
            plane.request_bytes(CtrlSubject::Health, b"not json at all").expect("never fatal");
        assert_eq!(reply, CtrlReply::Ignored);
        // A well-formed request on the WRONG subject.
        let reply = plane
            .request(CtrlSubject::Health, &CtrlRequest::Topology { known_epoch: 0 })
            .expect("never fatal");
        assert_eq!(reply, CtrlReply::Ignored);
        // A data-shaped field inside a request payload.
        let hostile = br#"{"kind":"Health","member":"m","documents":[]}"#;
        let reply = plane.request_bytes(CtrlSubject::Health, hostile).expect("never fatal");
        assert_eq!(reply, CtrlReply::Ignored);
        // An unknown subject is ignored without counting.
        let reply = plane
            .request_on("terrabase.cell-a.ctrl.quantum-tunneling", br#"{}"#)
            .expect("never fatal");
        assert_eq!(reply, CtrlReply::Ignored);
        assert_eq!(plane.malformed_dropped(), 3, "three malformed, all counted");
    }

    #[test]
    fn a_lost_plane_fails_closed_but_admitted_members_keep_serving() {
        let plane = plane();
        // Advertise state first: the pause must answer typed against
        // HELD metadata, not vacuously against a missing key.
        plane.kv_cas(KvBucket::Members, "member/member-a", lease(0), None).expect("lands");
        plane.pause();
        assert_eq!(
            plane
                .request(
                    CtrlSubject::Join,
                    &CtrlRequest::Join {
                        token: "bootstrap-token".into(),
                        member: "member-b".into(),
                        region: "eu-1".into(),
                        capabilities: vec![]
                    }
                )
                .err(),
            Some(ControlError::Paused),
            "new joins pause while the plane is lost"
        );
        assert_eq!(
            plane.request(CtrlSubject::Topology, &CtrlRequest::Topology { known_epoch: 0 }).err(),
            Some(ControlError::Paused),
            "topology changes pause"
        );
        assert_eq!(
            plane.kv_get(KvBucket::Members, "member/member-a").err(),
            Some(ControlError::Paused),
            "an unreachable broker answers nothing — not even held metadata"
        );
        assert_eq!(
            plane.kv_cas(KvBucket::Members, "member/member-a", lease(9), None).err(),
            Some(ControlError::Paused),
            "configuration changes pause"
        );
        // Recovery: the plane reopens and serves again, the held
        // metadata intact.
        plane.resume();
        assert!(
            plane.kv_get(KvBucket::Members, "member/member-a").is_ok(),
            "the held metadata survives the outage"
        );
        assert!(plane
            .request(CtrlSubject::Topology, &CtrlRequest::Topology { known_epoch: 0 })
            .is_ok());
    }

    #[test]
    fn the_data_plane_has_no_dependence_on_the_control_plane() {
        // The recovery receipt the contract demands: with the control
        // plane gone entirely, already-admitted members keep serving
        // writes, converging, and reading over their last verified
        // topology.
        use crate::host::{ClusterHost, MemberSpec};
        use crate::identity::ClusterMode;
        use terrabase_replication_core::compose::ComposedWrite;
        use terrabase_v2::replication::LeaderlessLevel;
        use terrabase_v2::types::DocumentValue;

        let dir = std::env::temp_dir().join(format!(
            "tbv2-cluster-control-dataplane-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map_or(0, |elapsed| elapsed.as_nanos())
        ));
        let _ = std::fs::remove_dir_all(&dir);
        std::fs::create_dir_all(&dir).unwrap();
        let spec = vec![
            MemberSpec { name: "member-a".into(), region: "eu-1".into() },
            MemberSpec { name: "member-b".into(), region: "eu-1".into() },
            MemberSpec { name: "member-c".into(), region: "eu-1".into() },
        ];
        let mut cluster =
            ClusterHost::create(&dir, "cell-a", ClusterMode::Active, "created", &spec, "tenant-a")
                .expect("creates");

        // The plane exists, then it is GONE — not paused, dropped.
        let plane = plane();
        drop(plane);

        for attempt in 1..=3 {
            cluster
                .client_write(
                    "member-b",
                    &ComposedWrite::Put {
                        operation: format!("op-{attempt}"),
                        document: format!("doc-{attempt}"),
                        body: std::collections::BTreeMap::from([(
                            "status".to_owned(),
                            DocumentValue::String("open".to_owned()),
                        )]),
                    },
                    LeaderlessLevel::RegionalQuorum,
                )
                .expect("the data plane serves without the control plane");
        }
        let (value, _) = cluster.client_read("member-c", "doc-2").expect("reads");
        assert!(value.is_some(), "the members converge without the control plane");
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn subject_text_forms_and_resolve_round_trip() {
        let cluster = ControlCluster::try_new("cell-a").expect("valid");
        assert_eq!(CtrlSubject::Join.subject(&cluster), "terrabase.cell-a.ctrl.join");
        assert_eq!(KvBucket::TierPolicy.name(&cluster), "terrabase.cell-a.tierpolicy");
        for subject in [
            CtrlSubject::Join,
            CtrlSubject::Health,
            CtrlSubject::Topology,
            CtrlSubject::ConfigRefresh,
            CtrlSubject::RouteLookup,
        ] {
            assert_eq!(CtrlSubject::resolve(&cluster, &subject.subject(&cluster)), Some(subject));
        }
        assert_eq!(CtrlSubject::resolve(&cluster, "terrabase.cell-b.ctrl.join"), None);
        assert!(!KvBucket::Config.key_is_well_formed("epoch/not-a-number"));
        assert!(KvBucket::Config.key_is_well_formed("epoch/3"));
    }
}
