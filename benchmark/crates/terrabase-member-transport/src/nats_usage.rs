//! `TerraBase`'s NATS usage boundary: KV, config sharing, and
//! discoverability only — never a data store or data-plane transport.
//!
//! Charter PD-2026-08-26-13 D2 and ADR 0012 D-2: within `TerraBase`, NATS
//! serves exactly three roles — a key-value store (`JetStream` KV) for
//! operational key-value state, configuration sharing, and service/member
//! discoverability — under the Orchestrator NATS spec's separation rules
//! (own account and subject namespace; never the control plane's). No
//! database data — documents, change events, journal or replication frames,
//! sync checkpoints, or any other client or member payload — is stored in
//! or transported through NATS.
//!
//! This module is that boundary as code:
//!
//! - [`Subject`] forms only database-namespace subjects in the three role
//!   shapes; anything outside `terrabase.db.<cluster>.{kv,config,discovery}`
//!   fails closed at construction.
//! - [`RecordValue`] is a closed value set: every record this module can
//!   put is one of the three typed shapes whose fields are validated
//!   identifiers, plain numbers, or a socket address. There is no byte, and
//!   no free-text, payload parameter anywhere on the surface, so database
//!   data cannot ride it.
//! - [`NatsFixture`] implements the NATS-facing semantics these roles need
//!   — revisioned KV puts, compare-and-swap ownership transitions,
//!   monotonic config versions, heartbeated discovery with a lease-bounded
//!   healthy/stale/missing observation — with two accounts whose subject
//!   namespaces and credentials never mix. It is a fixture of the
//!   semantics, not a NATS server; a live `nats-server` run against the
//!   same typed surface is the deployment integration step recorded in the
//!   SE-06 evidence.
//! - [`DatabaseNats::audit`] is the explicit no-database-data audit: it
//!   walks everything the usage has stored and proves every subject is in
//!   the database namespace, every value is one of the closed kinds, and
//!   every field is an identifier, number, or address — rendering its
//!   verdict as a receipt artifact.

use std::cell::RefCell;
use std::collections::BTreeMap;
use std::fmt;
use std::fmt::Write as _;
use std::net::SocketAddr;

use ring::rand::{SecureRandom, SystemRandom};

use crate::identity::{MemberId, RegionId, validate_identifier};

/// The subject namespace prefix of `TerraBase`'s NATS usage: everything the
/// database account may touch lives under here.
pub const DATABASE_NATS_NAMESPACE: &str = "terrabase.db";

/// The control plane's namespace, named here only so the database usage can
/// be proven never to address it.
pub const CONTROL_PLANE_NAMESPACE: &str = "terrabase.orchestrator";

/// The discovery lease, in fixture ticks: a record whose last heartbeat is
/// older than this observes stale (the Orchestrator NATS spec's 20-second
/// lease as a fixture fact; a publish is a heartbeat).
pub const LEASE_TICKS: u64 = 20;

/// The closed range of the shared lane settings' catch-up batch (SE-07
/// budget E-2): the flow-control knob the config-sharing role distributes
/// cannot degenerate the lane — a batch below the floor starves catch-up
/// throughput, a batch above the ceiling breaks the bounded-link contract
/// (`transport::MAX_LINK_FRAMES` sized frames).
pub const FLOW_CONTROL_CATCH_UP_BATCH_RANGE: (u32, u32) = (8, 256);

/// One of the three NATS roles `TerraBase` is allowed (PD-2026-08-26-13 D2).
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub enum NatsRole {
    /// The `JetStream` KV role: operational key-value state.
    OperationalKV,
    /// The configuration-sharing role.
    ConfigSharing,
    /// The service/member discoverability role.
    Discoverability,
}

impl NatsRole {
    /// The subject token of the role.
    #[must_use]
    pub const fn subject_token(self) -> &'static str {
        match self {
            Self::OperationalKV => "kv",
            Self::ConfigSharing => "config",
            Self::Discoverability => "discovery",
        }
    }

    /// The canonical role name used by receipts and audits.
    #[must_use]
    pub const fn name(self) -> &'static str {
        match self {
            Self::OperationalKV => "operational-kv",
            Self::ConfigSharing => "config-sharing",
            Self::Discoverability => "discoverability",
        }
    }
}

/// One database-namespace subject in a role shape.
#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct Subject {
    text: String,
    role: NatsRole,
}

impl Subject {
    /// Validates one formed subject text and adopts it.
    ///
    /// # Errors
    ///
    /// Returns [`UsageError::OutsideDatabaseNamespace`] for another
    /// namespace's subject and [`UsageError::InvalidSubject`] for text that
    /// is not a well-formed `terrabase.db.<cluster>.<role>.<key...>`
    /// subject with valid NATS subject tokens (no whitespace, no
    /// wildcards — stricter than identifier text, so no document body can
    /// pose as a subject token).
    pub fn try_new(text: &str) -> Result<Self, UsageError> {
        let tokens: Vec<&str> = text.split('.').collect();
        if tokens.first() != Some(&"terrabase") {
            return Err(UsageError::InvalidSubject);
        }
        if tokens.get(1) != Some(&"db") {
            return Err(UsageError::OutsideDatabaseNamespace);
        }
        if tokens.len() < 5 {
            return Err(UsageError::InvalidSubject);
        }
        for token in &tokens[2..] {
            validate_nats_token(token).map_err(|_| UsageError::InvalidSubject)?;
        }
        let role = match tokens[3] {
            "kv" => NatsRole::OperationalKV,
            "config" => NatsRole::ConfigSharing,
            "discovery" => NatsRole::Discoverability,
            _ => return Err(UsageError::InvalidSubject),
        };
        Ok(Self { text: text.to_owned(), role })
    }

    /// Forms one operational-KV subject.
    ///
    /// # Errors
    ///
    /// Returns [`UsageError::InvalidSubject`] for invalid tokens.
    pub fn operational(cluster: &str, key: &str) -> Result<Self, UsageError> {
        Self::build(cluster, NatsRole::OperationalKV, key)
    }

    /// Forms one config-sharing subject.
    ///
    /// # Errors
    ///
    /// Returns [`UsageError::InvalidSubject`] for invalid tokens.
    pub fn config(cluster: &str, name: ConfigName) -> Result<Self, UsageError> {
        Self::build(cluster, NatsRole::ConfigSharing, name.name())
    }

    /// Forms one discovery subject.
    ///
    /// # Errors
    ///
    /// Returns [`UsageError::InvalidSubject`] for invalid tokens.
    pub fn discovery(cluster: &str, member: &str) -> Result<Self, UsageError> {
        Self::build(cluster, NatsRole::Discoverability, member)
    }

    fn build(cluster: &str, role: NatsRole, tail: &str) -> Result<Self, UsageError> {
        validate_nats_token(cluster).map_err(|_| UsageError::InvalidSubject)?;
        validate_nats_token(tail).map_err(|_| UsageError::InvalidSubject)?;
        Self::try_new(&format!(
            "{}.{cluster}.{}.{tail}",
            DATABASE_NATS_NAMESPACE,
            role.subject_token()
        ))
    }

    /// Builds one subject of another namespace without validating the
    /// namespace: the separation proof's probe value, so a test can hand a
    /// database client a foreign subject and observe the account boundary
    /// reject it. No database usage constructs subjects this way.
    #[must_use]
    pub fn audit_probe(text: &str) -> Self {
        let role = match text.split('.').nth(3) {
            Some("config") => NatsRole::ConfigSharing,
            Some("discovery") => NatsRole::Discoverability,
            _ => NatsRole::OperationalKV,
        };
        Self { text: text.to_owned(), role }
    }

    /// The full subject text.
    #[must_use]
    pub fn as_str(&self) -> &str {
        &self.text
    }

    /// The role the subject belongs to.
    #[must_use]
    pub const fn role(&self) -> NatsRole {
        self.role
    }
}

impl fmt::Display for Subject {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.text)
    }
}

/// One operational KV record: a data range's advertised write-path ownership
/// under its current epoch. Identities and numbers only.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct OperationalRecord {
    range: String,
    owner: MemberId,
    epoch: u64,
}

impl OperationalRecord {
    /// The advertised data range.
    #[must_use]
    pub fn range(&self) -> &str {
        &self.range
    }

    /// The advertised owning member.
    #[must_use]
    pub fn owner(&self) -> &MemberId {
        &self.owner
    }

    /// The advertised ownership epoch.
    #[must_use]
    pub const fn epoch(&self) -> u64 {
        self.epoch
    }
}

/// The named configuration documents `TerraBase` shares through NATS: a
/// closed set of typed settings, never free-form text.
#[derive(Clone, Copy, Debug, Eq, Hash, PartialEq)]
pub enum ConfigName {
    /// The member-lane settings document.
    LaneSettings,
}

impl ConfigName {
    /// The canonical name, used as the config subject's key token.
    #[must_use]
    pub const fn name(self) -> &'static str {
        match self {
            Self::LaneSettings => "lane-settings",
        }
    }
}

/// The member-lane settings: the typed configuration payload of the
/// [`ConfigName::LaneSettings`] document. Numbers only.
///
/// The socket lane once carried a pump drain window here; the lane's drain
/// is delivery-causal since the SE-06 repair, so the window knob is gone
/// rather than advertised as a setting that does nothing.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct LaneSettings {
    /// The bounded number of commits one catch-up round requests.
    pub catch_up_batch: u32,
}

/// One versioned configuration record.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct ConfigRecord {
    name: ConfigName,
    version: u64,
    settings: LaneSettings,
}

/// Reports whether a closed `(low, high)` range contains `value`.
const fn range_contains(range: (u32, u32), value: u32) -> bool {
    range.0 <= value && value <= range.1
}

impl ConfigRecord {
    /// Creates one config record; the version starts at one.
    ///
    /// # Errors
    ///
    /// Returns [`UsageError::InvalidRecord`] for a zero version or a
    /// flow-control setting outside its closed written range (SE-07
    /// budget E-2: the shared knob fails closed rather than degenerating
    /// the lane).
    pub fn new(name: ConfigName, version: u64, settings: LaneSettings) -> Result<Self, UsageError> {
        if version == 0
            || !range_contains(FLOW_CONTROL_CATCH_UP_BATCH_RANGE, settings.catch_up_batch)
        {
            return Err(UsageError::InvalidRecord);
        }
        Ok(Self { name, version, settings })
    }

    /// The config document's name.
    #[must_use]
    pub const fn name(&self) -> ConfigName {
        self.name
    }

    /// The record's version.
    #[must_use]
    pub const fn version(&self) -> u64 {
        self.version
    }

    /// The record's typed settings.
    #[must_use]
    pub const fn settings(&self) -> LaneSettings {
        self.settings
    }
}

/// One member's discovery record: seat facts and the engine-socket lane
/// endpoint it advertises. Connection metadata only — no member frame, no
/// document, no journal content can appear in it.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DiscoveryRecord {
    member: MemberId,
    region: RegionId,
    failure_domain: String,
    address: SocketAddr,
    generation: u64,
    observed_tick: u64,
}

impl DiscoveryRecord {
    /// Creates one discovery record (its observation tick is stamped by the
    /// publish, which is the heartbeat).
    ///
    /// # Errors
    ///
    /// Returns [`UsageError::InvalidRecord`] for invalid identifier tokens
    /// or a zero generation.
    pub fn new(
        member: &str,
        region: &str,
        failure_domain: &str,
        address: SocketAddr,
        generation: u64,
    ) -> Result<Self, UsageError> {
        let member = MemberId::try_new(member).map_err(|_| UsageError::InvalidRecord)?;
        let region = RegionId::try_new(region).map_err(|_| UsageError::InvalidRecord)?;
        validate_identifier(failure_domain).map_err(|_| UsageError::InvalidRecord)?;
        if generation == 0 {
            return Err(UsageError::InvalidRecord);
        }
        Ok(Self {
            member,
            region,
            failure_domain: failure_domain.to_owned(),
            address,
            generation,
            observed_tick: 0,
        })
    }

    /// The discovered member.
    #[must_use]
    pub fn member(&self) -> &MemberId {
        &self.member
    }

    /// The member's Region seat.
    #[must_use]
    pub fn region(&self) -> &RegionId {
        &self.region
    }

    /// The member's failure domain.
    #[must_use]
    pub fn failure_domain(&self) -> &str {
        &self.failure_domain
    }

    /// The advertised engine-socket lane endpoint.
    #[must_use]
    pub const fn address(&self) -> SocketAddr {
        self.address
    }

    /// The record's generation (raised by every re-join).
    #[must_use]
    pub const fn generation(&self) -> u64 {
        self.generation
    }
}

/// The closed value set of the NATS surface: every record `TerraBase` can
/// store is one of these three shapes, and none of them carries a byte
/// payload.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum RecordValue {
    /// One operational KV record.
    Operational(OperationalRecord),
    /// One versioned configuration record.
    Config(ConfigRecord),
    /// One member discovery record.
    Discovery(DiscoveryRecord),
}

impl RecordValue {
    /// Builds one range-ownership operational record.
    ///
    /// # Errors
    ///
    /// Returns [`UsageError::InvalidRecord`] for invalid identifiers or a
    /// zero epoch.
    pub fn operational_range_ownership(
        range: &str,
        owner: &str,
        epoch: u64,
    ) -> Result<Self, UsageError> {
        validate_identifier(range).map_err(|_| UsageError::InvalidRecord)?;
        let owner = MemberId::try_new(owner).map_err(|_| UsageError::InvalidRecord)?;
        if epoch == 0 {
            return Err(UsageError::InvalidRecord);
        }
        Ok(Self::Operational(OperationalRecord { range: range.to_owned(), owner, epoch }))
    }

    /// The canonical kind name (receipts and audits render this, never an
    /// enum variant that could hint at a payload).
    #[must_use]
    pub const fn kind(&self) -> &'static str {
        match self {
            Self::Operational(_) => "operational",
            Self::Config(_) => "config",
            Self::Discovery(_) => "discovery",
        }
    }
}

/// One stored KV entry with its revision.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct StoredRecord {
    /// The revision of this entry (put count at the subject).
    pub revision: u64,
    /// The stored record.
    pub value: RecordValue,
}

/// The lease-bounded observation states of a discovery record (the
/// Orchestrator NATS spec's healthy/stale/missing vocabulary).
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ObservationState {
    /// The record's heartbeat is inside the lease.
    Healthy,
    /// The record exists but its heartbeat expired.
    Stale,
    /// The roster names the member but no record exists.
    Missing,
}

/// Every way the NATS usage boundary fails closed.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum UsageError {
    /// The subject text is not a well-formed database-role subject.
    InvalidSubject,
    /// The subject belongs to a namespace the database usage must never
    /// address.
    OutsideDatabaseNamespace,
    /// A record constructor rejected its input.
    InvalidRecord,
    /// The client's account may not address the subject (account and
    /// namespace separation).
    CrossAccountSubject,
    /// The credential does not authenticate the account.
    BadCredentials,
    /// The compare-and-swap did not hold.
    CasConflict,
    /// The configuration version did not advance past the stored one.
    StaleConfigVersion,
    /// No record exists at the addressed subject.
    NotFound,
    /// The fixture's randomness source failed.
    Randomness,
}

/// One NATS account of the fixture: an identity, its allowed subject
/// prefix, and its credentials.
struct Account {
    id: AccountId,
    prefix: String,
    credentials: Vec<Credential>,
}

/// An account identity.
#[derive(Clone, Debug, Eq, Hash, PartialEq)]
pub struct AccountId {
    name: String,
}

impl fmt::Display for AccountId {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.name)
    }
}

/// One account credential. Generated per account; two accounts never hold
/// the same credential.
#[derive(Clone, Debug, Eq, Hash, PartialEq)]
pub struct Credential {
    token: String,
}

impl fmt::Display for Credential {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.token)
    }
}

/// The audit's verdict over everything one usage stored.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct AuditReport {
    /// The checks performed, as (name, passed) pairs.
    pub checks: Vec<(&'static str, bool)>,
}

impl AuditReport {
    /// Reports whether every check passed.
    #[must_use]
    pub fn passed(&self) -> bool {
        self.checks.iter().all(|(_, passed)| *passed)
    }

    /// Renders the audit as its receipt artifact: one line per check and a
    /// claim line that stays inside the lane.
    #[must_use]
    pub fn render(&self) -> String {
        let mut out = String::from("nats usage audit\n");
        for (name, passed) in &self.checks {
            let _ = writeln!(out, "check: {name}: {}", if *passed { "pass" } else { "fail" });
        }
        out.push_str("claim: none beyond the usage boundary; no database data rides NATS\n");
        out
    }
}

struct FixtureState {
    accounts: Vec<Account>,
    kv: BTreeMap<String, StoredRecord>,
    configs: BTreeMap<&'static str, ConfigRecord>,
    discovery: BTreeMap<String, DiscoveryRecord>,
    tick: u64,
}

/// The in-fixture NATS-facing semantics of the three allowed roles, with
/// two separated accounts.
///
/// A fixture of the semantics, not a NATS server: it proves the typed
/// surface, the namespace and account separation, the revision/CAS/version/
/// lease rules, and the audit. The live `nats-server` run against the same
/// typed surface is the deployment integration step recorded in the SE-06
/// evidence.
pub struct NatsFixture {
    state: RefCell<FixtureState>,
}

impl NatsFixture {
    /// Builds the fixture with the database account (namespace
    /// `terrabase.db.`) and a distinct control-plane account (namespace
    /// `terrabase.orchestrator.`), each with its own generated credential.
    ///
    /// # Errors
    ///
    /// Returns [`UsageError::Randomness`] when a credential cannot be
    /// generated.
    pub fn new() -> Result<Self, UsageError> {
        let database_credential = generate_credential()?;
        let control_plane_credential = generate_credential()?;
        let state = FixtureState {
            accounts: vec![
                Account {
                    id: AccountId { name: "terrabase-database".to_owned() },
                    prefix: format!("{DATABASE_NATS_NAMESPACE}."),
                    credentials: vec![database_credential],
                },
                Account {
                    id: AccountId { name: "terrabase-orchestrator".to_owned() },
                    prefix: format!("{CONTROL_PLANE_NAMESPACE}."),
                    credentials: vec![control_plane_credential],
                },
            ],
            kv: BTreeMap::new(),
            configs: BTreeMap::new(),
            discovery: BTreeMap::new(),
            tick: 0,
        };
        Ok(Self { state: RefCell::new(state) })
    }

    /// Returns the database account's identity.
    #[must_use]
    pub fn database_account(&self) -> AccountId {
        AccountId { name: "terrabase-database".to_owned() }
    }

    /// Returns the control-plane account's identity (named so the database
    /// usage can be proven separate from it).
    #[must_use]
    pub fn control_plane_account(&self) -> AccountId {
        AccountId { name: "terrabase-orchestrator".to_owned() }
    }

    /// Returns every credential an account holds.
    #[must_use]
    pub fn credentials_of(&self, account: &AccountId) -> Vec<Credential> {
        self.state
            .borrow()
            .accounts
            .iter()
            .find(|candidate| &candidate.id == account)
            .map_or_else(Vec::new, |found| found.credentials.clone())
    }

    /// Authenticates the database account's client.
    ///
    /// # Errors
    ///
    /// Returns [`UsageError::BadCredentials`] when the credential does not
    /// authenticate the account.
    pub fn database_client(&self) -> Result<DatabaseNats<'_>, UsageError> {
        let account = self.database_account();
        let credential =
            self.credentials_of(&account).first().cloned().ok_or(UsageError::BadCredentials)?;
        self.client_for(&account, &credential)
    }

    /// Authenticates one account's client with its credential.
    ///
    /// # Errors
    ///
    /// Returns [`UsageError::BadCredentials`] when the credential does not
    /// authenticate the account.
    pub fn client_for(
        &self,
        account: &AccountId,
        credential: &Credential,
    ) -> Result<DatabaseNats<'_>, UsageError> {
        let state = self.state.borrow();
        let found = state
            .accounts
            .iter()
            .find(|candidate| &candidate.id == account)
            .ok_or(UsageError::BadCredentials)?;
        if !found.credentials.contains(credential) {
            return Err(UsageError::BadCredentials);
        }
        let prefix = found.prefix.clone();
        drop(state);
        Ok(DatabaseNats {
            fixture: self,
            account: account.clone(),
            credential: credential.clone(),
            allowed_prefix: prefix,
        })
    }

    /// Advances the fixture clock one tick (discovery leases are measured
    /// in these ticks).
    pub fn tick(&self) {
        self.state.borrow_mut().tick += 1;
    }

    /// Inserts one entry under a foreign subject (the audit-failure probe:
    /// the audit must notice anything outside the database namespace).
    ///
    /// # Panics
    ///
    /// Panics when the probe's own seed record is invalid (a fixed probe
    /// value).
    pub fn debug_insert_foreign_subject(&self) {
        self.state.borrow_mut().kv.insert(
            "terrabase.foreign.cell-a.kv.probe".to_owned(),
            StoredRecord {
                revision: 1,
                value: RecordValue::operational_range_ownership("main", "m-a1", 1)
                    .expect("valid record"),
            },
        );
    }
}

/// One authenticated client of the NATS-facing surface.
pub struct DatabaseNats<'a> {
    fixture: &'a NatsFixture,
    account: AccountId,
    credential: Credential,
    allowed_prefix: String,
}

impl DatabaseNats<'_> {
    /// The authenticated account.
    #[must_use]
    pub fn account(&self) -> &AccountId {
        &self.account
    }

    /// Puts one record at an operational-KV subject, returning the new
    /// revision.
    ///
    /// # Errors
    ///
    /// Returns [`UsageError::CrossAccountSubject`] when the subject is
    /// outside this account's namespace and [`UsageError::InvalidSubject`]
    /// when it is not an operational-KV subject.
    pub fn put(&self, subject: &Subject, value: RecordValue) -> Result<u64, UsageError> {
        self.guard(subject, NatsRole::OperationalKV)?;
        let mut state = self.fixture.state.borrow_mut();
        let revision = state.kv.get(subject.as_str()).map_or(1, |stored| stored.revision + 1);
        state.kv.insert(subject.as_str().to_owned(), StoredRecord { revision, value });
        Ok(revision)
    }

    /// Reads one stored record.
    ///
    /// # Errors
    ///
    /// Returns [`UsageError::CrossAccountSubject`] or
    /// [`UsageError::NotFound`].
    pub fn get(&self, subject: &Subject) -> Result<StoredRecord, UsageError> {
        self.guard(subject, subject.role())?;
        self.fixture.state.borrow().kv.get(subject.as_str()).cloned().ok_or(UsageError::NotFound)
    }

    /// Transitions one range's advertised ownership by compare-and-swap: the
    /// caller names the epoch the transition commits at, and the swap holds
    /// only when that epoch is exactly one past the stored epoch (and the
    /// new epoch matches it) — a stale, a deposed, or a non-adjacent claim
    /// is fenced with [`UsageError::CasConflict`], never silently applied.
    ///
    /// # Errors
    ///
    /// Returns the usage's typed failures.
    pub fn transition_ownership(
        &self,
        cluster: &str,
        range: &str,
        new_owner: &str,
        expected_epoch: u64,
        new_epoch: u64,
    ) -> Result<(), UsageError> {
        let subject = Subject::operational(cluster, &format!("ownership/{range}"))?;
        let mut state = self.fixture.state.borrow_mut();
        let current = state
            .kv
            .get(subject.as_str())
            .and_then(|stored| match &stored.value {
                RecordValue::Operational(record) => Some(record.epoch()),
                _ => None,
            })
            .unwrap_or(0);
        if expected_epoch != new_epoch || new_epoch != current + 1 {
            return Err(UsageError::CasConflict);
        }
        let record = RecordValue::operational_range_ownership(range, new_owner, new_epoch)?;
        let revision = state.kv.get(subject.as_str()).map_or(1, |stored| stored.revision + 1);
        state.kv.insert(subject.as_str().to_owned(), StoredRecord { revision, value: record });
        Ok(())
    }

    /// Publishes one config version; a version that does not advance past
    /// the stored one is fenced.
    ///
    /// # Errors
    ///
    /// Returns [`UsageError::StaleConfigVersion`] when the version does not
    /// advance.
    pub fn publish_config(&self, record: ConfigRecord) -> Result<(), UsageError> {
        let subject = Subject::config("cell-a", record.name())?;
        self.guard(&subject, NatsRole::ConfigSharing)?;
        let mut state = self.fixture.state.borrow_mut();
        if let Some(stored) = state.configs.get(record.name().name())
            && record.version() <= stored.version()
        {
            return Err(UsageError::StaleConfigVersion);
        }
        state.configs.insert(record.name().name(), record);
        Ok(())
    }

    /// Reads the latest config record of one name.
    ///
    /// # Errors
    ///
    /// Returns [`UsageError::NotFound`] when nothing was published.
    pub fn latest_config(&self, name: ConfigName) -> Result<ConfigRecord, UsageError> {
        self.fixture.state.borrow().configs.get(name.name()).copied().ok_or(UsageError::NotFound)
    }

    /// Publishes one member's discovery record — the heartbeat that renews
    /// its lease.
    ///
    /// # Errors
    ///
    /// Returns the usage's typed failures.
    pub fn publish_discovery(
        &self,
        cluster: &str,
        record: &DiscoveryRecord,
    ) -> Result<(), UsageError> {
        let subject = Subject::discovery(cluster, record.member().as_str())?;
        self.guard(&subject, NatsRole::Discoverability)?;
        let mut state = self.fixture.state.borrow_mut();
        let mut stamped = record.clone();
        stamped.observed_tick = state.tick;
        state.discovery.insert(subject.as_str().to_owned(), stamped);
        Ok(())
    }

    /// Observes the roster's discovery states at the fixture's current
    /// tick: healthy inside the lease, stale past it, missing without a
    /// record.
    ///
    /// # Panics
    ///
    /// Panics when a roster name is not a valid member identifier (the
    /// roster is caller input of the observation probe).
    #[must_use]
    pub fn discover(
        &self,
        cluster: &str,
        roster: &[&str],
    ) -> Vec<(DiscoveryRecord, ObservationState)> {
        let state = self.fixture.state.borrow();
        let now = state.tick;
        roster
            .iter()
            .map(|member| {
                let subject = Subject::discovery(cluster, member).expect("valid roster member");
                match state.discovery.get(subject.as_str()) {
                    Some(record) => {
                        let state = if now.saturating_sub(record.observed_tick) <= LEASE_TICKS {
                            ObservationState::Healthy
                        } else {
                            ObservationState::Stale
                        };
                        (record.clone(), state)
                    }
                    None => (
                        DiscoveryRecord::new(
                            member,
                            "r-unknown",
                            "fd-unknown",
                            ([127, 0, 0, 1], 0).into(),
                            1,
                        )
                        .expect("valid placeholder"),
                        ObservationState::Missing,
                    ),
                }
            })
            .collect()
    }

    /// Runs the no-database-data audit over everything this fixture holds:
    /// every subject inside the database namespace in a role shape, every
    /// value one of the closed kinds, every field an identifier, a number,
    /// or an address, and the accounts' credentials disjoint.
    #[must_use]
    pub fn audit(&self) -> AuditReport {
        let state = self.fixture.state.borrow();
        let namespace_ok = state.kv.keys().all(|subject| {
            subject.starts_with(&self.allowed_prefix) && Subject::try_new(subject).is_ok()
        }) && state.discovery.keys().all(|subject| {
            subject.starts_with(&self.allowed_prefix) && Subject::try_new(subject).is_ok()
        });
        let closed_set_ok = state.kv.values().all(|stored| {
            matches!(
                &stored.value,
                RecordValue::Operational(_) | RecordValue::Config(_) | RecordValue::Discovery(_)
            )
        }) && state
            .configs
            .values()
            .all(|record| matches!(record, ConfigRecord { .. }))
            && state.discovery.values().all(|record| matches!(record, DiscoveryRecord { .. }));
        let raw_byte_path_ok =
            state.kv.values().all(|stored| record_fields_validate(&stored.value))
                && state
                    .discovery
                    .values()
                    .all(|record| record_fields_validate(&RecordValue::Discovery(record.clone())));
        let mut credential_sets: Vec<Vec<Credential>> = Vec::new();
        for account in &state.accounts {
            credential_sets.push(account.credentials.clone());
        }
        let separation_ok = credential_sets.iter().enumerate().all(|(index, set)| {
            credential_sets.iter().enumerate().all(|(other, other_set)| {
                index == other || !set.iter().any(|c| other_set.contains(c))
            })
        }) && state
            .accounts
            .iter()
            .filter(|account| account.prefix != self.allowed_prefix)
            .all(|account| !state.kv.keys().any(|subject| subject.starts_with(&account.prefix)));
        AuditReport {
            checks: vec![
                ("subject namespace", namespace_ok),
                ("closed value set", closed_set_ok),
                ("no raw byte path", raw_byte_path_ok),
                ("account separation", separation_ok),
            ],
        }
    }

    /// Renders everything stored (subjects and record facts) — the audit's
    /// content scan surface.
    #[must_use]
    pub fn stored_rendering(&self) -> String {
        let state = self.fixture.state.borrow();
        let mut out = String::new();
        for (subject, stored) in &state.kv {
            let _ = writeln!(out, "{subject} {}", render_record(&stored.value));
        }
        for (subject, record) in &state.discovery {
            let _ = writeln!(
                out,
                "{subject} {}",
                render_record(&RecordValue::Discovery(record.clone()))
            );
        }
        for record in state.configs.values() {
            let _ = writeln!(out, "config {}", record.name().name());
        }
        out
    }

    /// Rejects a subject outside this client's account namespace or outside
    /// the expected role.
    fn guard(&self, subject: &Subject, role: NatsRole) -> Result<(), UsageError> {
        if !subject.as_str().starts_with(&self.allowed_prefix) {
            return Err(UsageError::CrossAccountSubject);
        }
        if subject.role() != role {
            return Err(UsageError::InvalidSubject);
        }
        let _ = &self.credential;
        Ok(())
    }
}

/// Renders one record as its audit-safe facts, or `None` when the record is
/// not one of the closed kinds.
fn render_record(record: &RecordValue) -> String {
    match record {
        RecordValue::Operational(record) => format!(
            "operational: range={} owner={} epoch={}",
            record.range(),
            record.owner(),
            record.epoch()
        ),
        RecordValue::Config(record) => format!(
            "config: name={} version={} catch-up-batch={}",
            record.name().name(),
            record.version(),
            record.settings().catch_up_batch,
        ),
        RecordValue::Discovery(record) => format!(
            "discovery: member={} region={} failure-domain={} address={} generation={}",
            record.member(),
            record.region(),
            record.failure_domain(),
            record.address(),
            record.generation(),
        ),
    }
}

/// Re-validates every field of a record: identifiers stay valid, numbers
/// stay numbers, addresses stay addresses — there is nowhere a byte payload
/// could hide.
fn record_fields_validate(record: &RecordValue) -> bool {
    match record {
        RecordValue::Operational(record) => {
            validate_identifier(record.range()).is_ok() && record.epoch() > 0
        }
        RecordValue::Config(record) => record.version() > 0 && record.settings().catch_up_batch > 0,
        RecordValue::Discovery(record) => {
            validate_identifier(record.member().as_str()).is_ok()
                && validate_identifier(record.region().as_str()).is_ok()
                && validate_identifier(record.failure_domain()).is_ok()
                && record.generation() > 0
        }
    }
}

/// Generates one account-bound credential token.
fn generate_credential() -> Result<Credential, UsageError> {
    let random = SystemRandom::new();
    let mut bytes = [0_u8; 16];
    random.fill(&mut bytes).map_err(|_| UsageError::Randomness)?;
    let token = bytes.iter().fold(String::with_capacity(32), |mut acc, byte| {
        use std::fmt::Write as _;
        let _ = write!(acc, "{byte:02x}");
        acc
    });
    Ok(Credential { token })
}

/// The error of a token that cannot be a NATS subject token.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct TokenError;

/// Validates one NATS subject token: non-empty, no whitespace anywhere, no
/// wildcard characters, and within the identifier byte bound. Stricter than
/// identifier text — a NATS subject is a wire token, and document-shaped
/// text never passes.
fn validate_nats_token(token: &str) -> Result<(), TokenError> {
    if token.is_empty()
        || token.len() > 255
        || token.bytes().any(|byte| {
            byte.is_ascii_whitespace() || byte == b'*' || byte == b'>' || byte.is_ascii_control()
        })
    {
        return Err(TokenError);
    }
    Ok(())
}
