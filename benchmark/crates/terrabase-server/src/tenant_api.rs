//! Typed D-51 tenant management service.
//!
//! A tenant is a database.  This module deliberately stops at a composition
//! boundary: durable database, control, key, usage, and authorization ports
//! are supplied by the member host.  There is no in-memory tenant catalogue
//! or local placeholder.  If a required port is absent, the operation fails
//! closed with [`TenantApiError::AdapterUnavailable`].
//!
//! The service is intentionally HTTP-framework agnostic.  [`TENANT_ROUTES`]
//! is the proposed parent-router/OpenAPI mapping; the parent HTTP module can
//! bind those operations without this module deciding request extraction,
//! status-code envelopes, or adding an unreviewed route.

use std::fmt;
use std::sync::Arc;

use terrabase::regions::RegionCode;

/// The largest page a tenant endpoint will ask a durable adapter to produce.
pub const MAX_PAGE_SIZE: u16 = 100;
/// The largest identifier accepted by this bounded API surface.
pub const MAX_IDENTIFIER_BYTES: usize = 128;
/// The largest opaque cursor accepted from a caller.
pub const MAX_CURSOR_BYTES: usize = 256;
/// The largest idempotency key accepted from a caller.
pub const MAX_IDEMPOTENCY_KEY_BYTES: usize = 128;
/// The largest tenant-template placement.
pub const MAX_PLACEMENT_REGIONS: usize = 20;
/// The largest transient plaintext key returned by a key adapter.
pub const MAX_KEY_SECRET_BYTES: usize = 4096;

/// A bounded identifier used for tenant and template names.
#[derive(Clone, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub struct Identifier(String);

impl Identifier {
    /// Validates and constructs an identifier. Identifiers are ASCII
    /// path-safe values; input is never normalized.
    ///
    /// # Errors
    ///
    /// Returns `InvalidInput("identifier")` for an empty, oversized, or invalid spelling.
    pub fn try_new(value: impl Into<String>) -> Result<Self, TenantApiError> {
        let value = value.into();
        if value.is_empty()
            || value.len() > MAX_IDENTIFIER_BYTES
            || !value.bytes().all(|byte| {
                byte.is_ascii_lowercase() || byte.is_ascii_digit() || matches!(byte, b'-' | b'_')
            })
            || matches!(value.as_bytes().first(), Some(b'-' | b'_'))
            || matches!(value.as_bytes().last(), Some(b'-' | b'_'))
        {
            return Err(TenantApiError::InvalidInput("identifier"));
        }
        Ok(Self(value))
    }

    /// Returns the canonical caller-visible spelling.
    #[must_use]
    pub fn as_str(&self) -> &str {
        &self.0
    }
}

impl fmt::Display for Identifier {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(&self.0)
    }
}

/// A tenant identity. It is also the database identity: no second tenant
/// storage concept is introduced by D-51.
pub type TenantId = Identifier;
/// A tenant-template identity.
pub type TenantTemplateId = Identifier;

/// A bounded opaque idempotency key. Durable mutation ports must persist and
/// replay the result for the same key and request fingerprint.
#[derive(Clone, Debug, Eq, Hash, PartialEq)]
pub struct IdempotencyKey(String);

impl IdempotencyKey {
    /// Validates and constructs an idempotency key.
    ///
    /// # Errors
    ///
    /// Returns `InvalidInput("idempotency_key")` for an empty or oversized key,
    /// non-ASCII text, whitespace, or control bytes.
    pub fn try_new(value: impl Into<String>) -> Result<Self, TenantApiError> {
        let value = value.into();
        if value.is_empty()
            || value.len() > MAX_IDEMPOTENCY_KEY_BYTES
            || !value.is_ascii()
            || value.bytes().any(|byte| byte.is_ascii_whitespace() || byte.is_ascii_control())
        {
            return Err(TenantApiError::InvalidInput("idempotency_key"));
        }
        Ok(Self(value))
    }

    /// Returns the key passed to durable ports.
    #[must_use]
    pub fn as_str(&self) -> &str {
        &self.0
    }
}

/// A bounded opaque page cursor returned by a durable adapter.
#[derive(Clone, Debug, Eq, Hash, PartialEq)]
pub struct PageCursor(String);

impl PageCursor {
    /// Validates and constructs a cursor. Cursors are opaque and are never
    /// parsed or used as filesystem paths by this service.
    ///
    /// # Errors
    ///
    /// Returns `InvalidInput("cursor")` for an empty or oversized cursor,
    /// non-ASCII text, whitespace, or control bytes.
    pub fn try_new(value: impl Into<String>) -> Result<Self, TenantApiError> {
        let value = value.into();
        if value.is_empty()
            || value.len() > MAX_CURSOR_BYTES
            || !value.is_ascii()
            || value.bytes().any(|byte| byte.is_ascii_whitespace() || byte.is_ascii_control())
        {
            return Err(TenantApiError::InvalidInput("cursor"));
        }
        Ok(Self(value))
    }

    /// Returns the opaque cursor passed to a durable port.
    #[must_use]
    pub fn as_str(&self) -> &str {
        &self.0
    }
}

/// A bounded page request shared by all list methods.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct PageRequest {
    /// Maximum number of records requested.
    pub limit: u16,
    /// Cursor after which records should be returned.
    pub cursor: Option<PageCursor>,
}

impl PageRequest {
    /// Builds a page request, refusing zero and unbounded page sizes.
    ///
    /// # Errors
    ///
    /// Returns `InvalidInput("page_size")` unless the limit is in `1..=MAX_PAGE_SIZE`.
    pub fn try_new(limit: u16, cursor: Option<PageCursor>) -> Result<Self, TenantApiError> {
        if !(1..=MAX_PAGE_SIZE).contains(&limit) {
            return Err(TenantApiError::InvalidInput("page_size"));
        }
        Ok(Self { limit, cursor })
    }

    fn validate(&self) -> Result<(), TenantApiError> {
        if !(1..=MAX_PAGE_SIZE).contains(&self.limit) {
            return Err(TenantApiError::InvalidInput("page_size"));
        }
        Ok(())
    }
}

/// A bounded page returned by a durable adapter.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Page<T> {
    /// Records in stable adapter order.
    pub items: Vec<T>,
    /// Cursor for the next page, if more records exist.
    pub next_cursor: Option<PageCursor>,
}

/// Immutable placement and residency lock inherited by tenant databases.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct PlacementLock {
    /// Explicit regions in which the database may exist.
    pub placement: Vec<RegionCode>,
    /// Immutable residency constraint, if one was selected.
    pub residency: Option<String>,
}

impl PlacementLock {
    fn validate(&self) -> Result<(), TenantApiError> {
        if self.placement.is_empty() || self.placement.len() > MAX_PLACEMENT_REGIONS {
            return Err(TenantApiError::InvalidInput("placement"));
        }
        let mut seen = Vec::with_capacity(self.placement.len());
        for region in &self.placement {
            if seen.contains(region) {
                return Err(TenantApiError::InvalidInput("placement"));
            }
            seen.push(*region);
        }
        if let Some(residency) = &self.residency
            && (residency.is_empty()
                || residency.len() > MAX_IDENTIFIER_BYTES
                || !residency.is_ascii())
        {
            return Err(TenantApiError::InvalidInput("residency"));
        }
        Ok(())
    }

    fn child(&self, requested: Option<&PlacementLock>) -> Result<Self, TenantApiError> {
        let child = requested.cloned().unwrap_or_else(|| self.clone());
        child.validate()?;
        if child.placement.iter().any(|region| !self.placement.contains(region)) {
            return Err(TenantApiError::PlacementLockViolation);
        }
        if child.residency != self.residency {
            return Err(TenantApiError::PlacementLockViolation);
        }
        Ok(child)
    }
}

/// The key policy inherited by databases from their tenant template.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct TenantKeyPolicy {
    /// Whether generated keys admit reads.
    pub read: bool,
    /// Whether generated keys admit writes.
    pub write: bool,
    /// Whether generated keys admit administration.
    pub admin: bool,
    /// Maximum key lifetime in seconds. Zero means the adapter's bounded
    /// platform default; an unbounded tenant key is never permitted here.
    pub expires_in_seconds: u64,
}

impl TenantKeyPolicy {
    fn validate(&self) -> Result<(), TenantApiError> {
        if !self.read && !self.write && !self.admin || self.expires_in_seconds == 0 {
            return Err(TenantApiError::InvalidInput("key_policy"));
        }
        Ok(())
    }
}

/// A durable tenant template DTO.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct TenantTemplate {
    /// Template identifier.
    pub id: TenantTemplateId,
    /// Tenant that owns this template. Listing always names this owner.
    pub owner_tenant: TenantId,
    /// Immutable placement lock inherited by each database.
    pub placement_lock: PlacementLock,
    /// Key policy inherited by each database.
    pub key_policy: TenantKeyPolicy,
}

/// Input for template creation.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct CreateTenantTemplate {
    /// New template identifier.
    pub id: TenantTemplateId,
    /// Owning tenant identity.
    pub owner_tenant: TenantId,
    /// Lock copied into every database using this template.
    pub placement_lock: PlacementLock,
    /// Key policy copied into every database using this template.
    pub key_policy: TenantKeyPolicy,
}

/// A durable database representation of one tenant.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct TenantDatabase {
    /// Tenant owner/project scope used for scoped list authorization.
    pub owner_tenant: TenantId,
    /// Tenant identity, equal to the database identity.
    pub tenant_id: TenantId,
    /// Template used to create this database.
    pub template_id: TenantTemplateId,
    /// Effective immutable placement lock.
    pub placement_lock: PlacementLock,
    /// Parent tenant when this database is a support branch.
    pub parent_tenant: Option<TenantId>,
}

/// Input for tenant/database creation. Placement is intentionally absent:
/// it can only be inherited from the selected template.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct CreateTenant {
    /// Tenant that owns the new database and template lookup scope.
    pub owner_tenant: TenantId,
    /// New tenant identity, also used as the database identity.
    pub tenant_id: TenantId,
    /// Existing template to inherit.
    pub template_id: TenantTemplateId,
}

/// Input for a support branch. A branch may narrow placement, but cannot
/// add a region or alter the source residency lock.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct CreateTenantBranch {
    /// New branch tenant/database identity.
    pub branch_tenant_id: TenantId,
    /// Optional narrowed placement and exact inherited residency.
    pub placement_lock: Option<PlacementLock>,
}

/// A tenant-scoped API key. The secret is returned by a successful mint only
/// and is not present in any DTO accepted by a durable port.
#[derive(Eq, PartialEq)]
pub struct TenantApiKey {
    /// Stable key identifier safe to retain and display.
    pub id: Identifier,
    /// Plaintext key material. Callers must show/store it immediately; the
    /// service never provides a second retrieval method.
    pub secret: String,
}

impl fmt::Debug for TenantApiKey {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter
            .debug_struct("TenantApiKey")
            .field("id", &self.id)
            .field("secret", &"[redacted]")
            .finish()
    }
}

/// Per-tenant usage facts. Values are already bounded and attributed by the
/// usage adapter to the requested tenant/database.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct TenantUsage {
    /// Tenant/database identity.
    pub tenant_id: TenantId,
    /// Start of the usage window, unix milliseconds.
    pub window_start_millis: u64,
    /// End of the usage window, unix milliseconds.
    pub window_end_millis: u64,
    /// Count of durable writes.
    pub writes: u64,
    /// Stored bytes charged to this tenant.
    pub storage_bytes: u64,
}

/// Operations checked by the explicit authorization port.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum TenantOperation {
    /// Create a template owned by one tenant.
    CreateTemplate,
    /// Read one template.
    ReadTemplate,
    /// List templates for one owner tenant.
    ListTemplates,
    /// Delete one template.
    DeleteTemplate,
    /// Create one tenant/database.
    CreateTenant,
    /// Read one tenant/database.
    ReadTenant,
    /// List databases of one exact tenant owner scope.
    ListTenants,
    /// Delete one tenant/database.
    DeleteTenant,
    /// Create one support branch.
    CreateBranch,
    /// Mint a key scoped to one tenant/database.
    MintKey,
    /// Read usage for one exact tenant/database.
    ReadUsage,
}

/// A caller context. The authorization adapter binds the principal to this
/// context; route values are never trusted as a replacement identity.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct TenantActor {
    /// Stable principal label, never key material or an external subject.
    pub principal: Identifier,
    /// Tenant context bound by the parent public API layer.
    pub tenant_id: TenantId,
}

/// A small, typed refusal vocabulary that does not leak tenant names,
/// adapter details, key bytes, or document data.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum TenantApiError {
    /// A bounded input failed validation.
    InvalidInput(&'static str),
    /// No credential was admitted for the operation.
    Unauthorized,
    /// The credential was admitted but not for this exact operation/scope.
    Forbidden,
    /// The requested tenant/template is absent. This is intentionally the
    /// same answer for an inaccessible object to prevent enumeration.
    NotFound,
    /// A durable create/delete or idempotency fingerprint conflicted.
    Conflict,
    /// The required durable adapter has not been supplied.
    AdapterUnavailable(&'static str),
    /// A source placement or residency lock would be widened.
    PlacementLockViolation,
    /// A key adapter returned material whose hash did not match its durable
    /// hash record, so the service refuses to expose it.
    KeyMaterialInvalid,
    /// A durable adapter returned a bounded operation failure.
    PortFailure(&'static str),
}

impl fmt::Display for TenantApiError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::InvalidInput(field) => write!(formatter, "invalid {field}"),
            Self::Unauthorized => formatter.write_str("unauthorized"),
            Self::Forbidden => formatter.write_str("forbidden"),
            Self::NotFound => formatter.write_str("not found"),
            Self::Conflict => formatter.write_str("conflict"),
            Self::AdapterUnavailable(port) => write!(formatter, "{port} adapter unavailable"),
            Self::PlacementLockViolation => formatter.write_str("placement lock violation"),
            Self::KeyMaterialInvalid => formatter.write_str("key material invalid"),
            Self::PortFailure(port) => write!(formatter, "{port} adapter failure"),
        }
    }
}

impl std::error::Error for TenantApiError {}

/// Errors an authorization adapter may return. They are intentionally
/// bounded so an external identity or query cannot enter an API error.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum AuthorizationError {
    /// No admitted principal exists.
    Unauthorized,
    /// The admitted principal lacks this operation for the target scope.
    Forbidden,
    /// The authorization source is unavailable.
    Unavailable,
}

/// The explicit durable authorization seam.
pub trait AuthorizationPort: Send + Sync {
    /// Authorizes one exact operation and target tenant before data access.
    ///
    /// # Errors
    ///
    /// Returns `Unauthorized`, `Forbidden`, or `Unavailable` when admission fails.
    fn authorize(
        &self,
        actor: &TenantActor,
        operation: TenantOperation,
        target_tenant: &TenantId,
    ) -> Result<(), AuthorizationError>;
}

/// Bounded durable-port errors.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum PortError {
    /// Record was not found, deliberately indistinguishable from an auth miss.
    NotFound,
    /// Create/delete or idempotency fingerprint conflicts.
    Conflict,
    /// Port has not reached a serving/available state.
    Unavailable,
    /// The request is refused by durable validation.
    Invalid,
    /// Other bounded durable failure.
    Failed,
}

/// Request passed to the durable control port for template creation.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct TemplateCreateCommand {
    /// Template body.
    pub template: CreateTenantTemplate,
    /// Durable idempotency key.
    pub idempotency_key: IdempotencyKey,
}

/// Request passed to the durable database port for tenant creation.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DatabaseCreateCommand {
    /// Tenant/database body with the inherited lock.
    pub database: TenantDatabase,
    /// Durable idempotency key.
    pub idempotency_key: IdempotencyKey,
}

/// Request passed to the durable database port for branch creation.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct BranchCreateCommand {
    /// Source tenant/database.
    pub source_tenant: TenantId,
    /// New branch database.
    pub branch: TenantDatabase,
    /// Durable idempotency key.
    pub idempotency_key: IdempotencyKey,
}

/// Request passed to the durable key port. Only the key adapter handles
/// plaintext generation; durable storage must retain only `secret_hash`.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct KeyMintCommand {
    /// Tenant/database scope.
    pub tenant_id: TenantId,
    /// Inherited policy.
    pub policy: TenantKeyPolicy,
    /// Durable idempotency key.
    pub idempotency_key: IdempotencyKey,
}

/// One-time key result from a key adapter. `secret_hash` is the hash stored
/// durably; `secret` is transient and is not returned by any get/list method.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct MintedKey {
    /// Stable key identifier.
    pub id: Identifier,
    /// Plaintext secret, returned exactly once through the service result.
    pub secret: String,
    /// BLAKE3 digest retained by the durable key store.
    pub secret_hash: [u8; 32],
}

/// Explicit durable database catalog and tenant/database operation port.
pub trait DatabasePort: Send + Sync {
    /// Creates one database-per-tenant record idempotently.
    ///
    /// # Errors
    ///
    /// Returns a durable conflict, invalid request, unavailable provider, or write failure.
    fn create(&self, command: &DatabaseCreateCommand) -> Result<TenantDatabase, PortError>;
    /// Reads exactly one tenant/database.
    ///
    /// # Errors
    ///
    /// Returns a durable provider or decoding failure; an absent record is `None`.
    fn get(&self, tenant_id: &TenantId) -> Result<Option<TenantDatabase>, PortError>;
    /// Lists only the explicitly named tenant owner scope.
    ///
    /// # Errors
    ///
    /// Returns an invalid cursor, unavailable provider, or durable read failure.
    fn list(
        &self,
        owner_tenant: &TenantId,
        page: &PageRequest,
    ) -> Result<Page<TenantDatabase>, PortError>;
    /// Deletes exactly one tenant/database idempotently.
    ///
    /// # Errors
    ///
    /// Returns a missing record, conflicting retry, unavailable provider, or deletion failure.
    fn delete(
        &self,
        tenant_id: &TenantId,
        idempotency_key: &IdempotencyKey,
    ) -> Result<(), PortError>;
    /// Creates a branch database idempotently.
    ///
    /// # Errors
    ///
    /// Returns a missing source, invalid placement, conflicting retry, or durable creation failure.
    fn create_branch(&self, command: &BranchCreateCommand) -> Result<TenantDatabase, PortError>;
}

/// Explicit durable control-plane template port.
pub trait ControlPort: Send + Sync {
    /// Creates a template idempotently.
    ///
    /// # Errors
    ///
    /// Returns an invalid request, conflicting retry, unavailable provider, or durable write failure.
    fn create_template(&self, command: &TemplateCreateCommand)
    -> Result<TenantTemplate, PortError>;
    /// Reads exactly one template in its owner scope.
    ///
    /// # Errors
    ///
    /// Returns a durable provider or decoding failure; an absent template is `None`.
    fn get_template(
        &self,
        owner_tenant: &TenantId,
        template_id: &TenantTemplateId,
    ) -> Result<Option<TenantTemplate>, PortError>;
    /// Lists templates only for the explicitly named owner tenant.
    ///
    /// # Errors
    ///
    /// Returns an invalid cursor, unavailable provider, or durable read failure.
    fn list_templates(
        &self,
        owner_tenant: &TenantId,
        page: &PageRequest,
    ) -> Result<Page<TenantTemplate>, PortError>;
    /// Deletes exactly one template idempotently.
    ///
    /// # Errors
    ///
    /// Returns a missing template, conflicting retry, unavailable provider, or deletion failure.
    fn delete_template(
        &self,
        owner_tenant: &TenantId,
        template_id: &TenantTemplateId,
        idempotency_key: &IdempotencyKey,
    ) -> Result<(), PortError>;
}

/// Explicit durable API-key port. Implementations generate key material,
/// hash it, persist the hash and scope atomically, then return plaintext only
/// in the transient [`MintedKey`] result.
pub trait ApiKeyPort: Send + Sync {
    /// Mints one tenant-scoped key from an inherited policy.
    ///
    /// # Errors
    ///
    /// Returns an invalid policy, conflicting retry, unavailable provider, or key persistence failure.
    fn mint(&self, command: &KeyMintCommand) -> Result<MintedKey, PortError>;
}

/// Explicit durable usage-facts port.
pub trait UsagePort: Send + Sync {
    /// Reads usage for one exact tenant/database and bounded time window.
    ///
    /// # Errors
    ///
    /// Returns a missing tenant, unavailable provider, or durable usage read failure.
    fn get(&self, tenant_id: &TenantId) -> Result<TenantUsage, PortError>;
}

/// All durable seams required by [`TenantService`]. Every field is optional
/// so startup composition can represent an absent adapter explicitly; the
/// service then refuses the affected call rather than creating local state.
#[derive(Default)]
pub struct TenantPorts {
    /// Durable database catalog.
    pub database: Option<Arc<dyn DatabasePort>>,
    /// Durable control-plane template catalog.
    pub control: Option<Arc<dyn ControlPort>>,
    /// Durable tenant-scoped key store.
    pub keys: Option<Arc<dyn ApiKeyPort>>,
    /// Durable usage facts.
    pub usage: Option<Arc<dyn UsagePort>>,
    /// Durable authorization source.
    pub authorization: Option<Arc<dyn AuthorizationPort>>,
}

/// D-51 typed tenant service. It performs authorization and lock validation,
/// then delegates all state to explicit durable ports.
pub struct TenantService {
    ports: TenantPorts,
}

impl TenantService {
    /// Creates a service from member-owned durable ports.
    #[must_use]
    pub fn new(ports: TenantPorts) -> Self {
        Self { ports }
    }

    /// Creates a handler-ready service from the complete production port set.
    ///
    /// Unlike [`Self::new`], this constructor rejects an incomplete
    /// composition at startup.  It is the constructor a serving HTTP router
    /// should use: every operation has a durable implementation and an
    /// authorization decision, and no in-process catalogue can be selected by
    /// accident.
    ///
    /// # Errors
    ///
    /// Returns `AdapterUnavailable` for the first missing required port.
    pub fn handler_ready(ports: TenantPorts) -> Result<Self, TenantApiError> {
        if ports.authorization.is_none() {
            return Err(TenantApiError::AdapterUnavailable("authorization"));
        }
        if ports.control.is_none() {
            return Err(TenantApiError::AdapterUnavailable("control"));
        }
        if ports.database.is_none() {
            return Err(TenantApiError::AdapterUnavailable("database"));
        }
        if ports.keys.is_none() {
            return Err(TenantApiError::AdapterUnavailable("key"));
        }
        if ports.usage.is_none() {
            return Err(TenantApiError::AdapterUnavailable("usage"));
        }
        Ok(Self::new(ports))
    }

    /// Returns a service that refuses every operation until real adapters are
    /// supplied. This is useful for startup wiring and fail-closed tests.
    #[must_use]
    pub fn without_adapters() -> Self {
        Self::new(TenantPorts::default())
    }

    /// Creates a tenant template in one owner's scope.
    ///
    /// # Errors
    ///
    /// Returns validation or authorization errors, missing-port or durable failures,
    /// or a control-port failure if the returned identity or policy differs.
    pub fn create_template(
        &self,
        actor: &TenantActor,
        command: &TemplateCreateCommand,
    ) -> Result<TenantTemplate, TenantApiError> {
        command.template.placement_lock.validate()?;
        command.template.key_policy.validate()?;
        validate_actor_target(actor, &command.template.owner_tenant)?;
        self.authorize(actor, TenantOperation::CreateTemplate, &command.template.owner_tenant)?;
        let template = self.control()?.create_template(command).map_err(map_port)?;
        // A durable adapter is part of the trust boundary.  Never expose a
        // row under a different owner or identity than the command that was
        // authorized, and never let it silently widen the lock/policy.
        if template.id != command.template.id
            || template.owner_tenant != command.template.owner_tenant
            || template.placement_lock != command.template.placement_lock
            || template.key_policy != command.template.key_policy
        {
            return Err(TenantApiError::PortFailure("control"));
        }
        Ok(template)
    }

    /// Gets one template in one owner's scope. Inaccessible and absent
    /// templates intentionally produce the same bounded [`TenantApiError::NotFound`].
    ///
    /// # Errors
    ///
    /// Returns scope, authorization, missing-port, or durable errors. An absent or
    /// mismatched template returns `NotFound`.
    pub fn get_template(
        &self,
        actor: &TenantActor,
        owner_tenant: &TenantId,
        template_id: &TenantTemplateId,
    ) -> Result<TenantTemplate, TenantApiError> {
        validate_actor_target(actor, owner_tenant)?;
        self.authorize(actor, TenantOperation::ReadTemplate, owner_tenant)?;
        let template = self
            .control()?
            .get_template(owner_tenant, template_id)
            .map_err(map_port)?
            .ok_or(TenantApiError::NotFound)?;
        if template.owner_tenant != *owner_tenant
            || template.id != *template_id
            || template.placement_lock.validate().is_err()
            || template.key_policy.validate().is_err()
        {
            return Err(TenantApiError::NotFound);
        }
        Ok(template)
    }

    /// Lists templates only for one explicitly named owner tenant.
    ///
    /// # Errors
    ///
    /// Returns invalid pagination, authorization, missing-port, or durable errors,
    /// or a port failure for an oversized or incorrectly scoped result.
    pub fn list_templates(
        &self,
        actor: &TenantActor,
        owner_tenant: &TenantId,
        page: &PageRequest,
    ) -> Result<Page<TenantTemplate>, TenantApiError> {
        page.validate()?;
        validate_actor_target(actor, owner_tenant)?;
        self.authorize(actor, TenantOperation::ListTemplates, owner_tenant)?;
        let result = self.control()?.list_templates(owner_tenant, page).map_err(map_port)?;
        validate_page(&result, page, |template| {
            if template.owner_tenant != *owner_tenant
                || template.placement_lock.validate().is_err()
                || template.key_policy.validate().is_err()
            {
                return Err(TenantApiError::PortFailure("control"));
            }
            Ok(())
        })?;
        Ok(result)
    }

    /// Deletes one template in one owner scope.
    ///
    /// # Errors
    ///
    /// Returns scope, authorization, missing-port, or durable deletion errors.
    pub fn delete_template(
        &self,
        actor: &TenantActor,
        owner_tenant: &TenantId,
        template_id: &TenantTemplateId,
        idempotency_key: &IdempotencyKey,
    ) -> Result<(), TenantApiError> {
        validate_actor_target(actor, owner_tenant)?;
        self.authorize(actor, TenantOperation::DeleteTemplate, owner_tenant)?;
        self.control()?
            .delete_template(owner_tenant, template_id, idempotency_key)
            .map_err(map_port)
    }

    /// Creates one tenant/database using exactly the selected template's
    /// placement and key policy. Caller-supplied placement is impossible in
    /// [`CreateTenant`].
    ///
    /// # Errors
    ///
    /// Returns scope, authorization, missing-template, placement, policy, or durable
    /// errors. A mismatched created database returns a database-port failure.
    pub fn create_tenant(
        &self,
        actor: &TenantActor,
        request: CreateTenant,
        idempotency_key: IdempotencyKey,
    ) -> Result<TenantDatabase, TenantApiError> {
        validate_actor_target(actor, &request.owner_tenant)?;
        self.authorize(actor, TenantOperation::CreateTenant, &request.owner_tenant)?;
        let template = self
            .control()?
            .get_template(&request.owner_tenant, &request.template_id)
            .map_err(map_port)?
            .ok_or(TenantApiError::NotFound)?;
        if template.owner_tenant != request.owner_tenant || template.id != request.template_id {
            return Err(TenantApiError::NotFound);
        }
        if template.placement_lock.validate().is_err() || template.key_policy.validate().is_err() {
            return Err(TenantApiError::PortFailure("control"));
        }
        let expected_owner = request.owner_tenant.clone();
        let expected_tenant = request.tenant_id.clone();
        let expected_template = request.template_id.clone();
        let expected_lock = template.placement_lock.clone();
        let database = TenantDatabase {
            owner_tenant: request.owner_tenant,
            tenant_id: request.tenant_id,
            template_id: template.id.clone(),
            placement_lock: template.placement_lock,
            parent_tenant: None,
        };
        let created = self
            .database()?
            .create(&DatabaseCreateCommand { database, idempotency_key })
            .map_err(map_port)?;
        if created.owner_tenant != expected_owner
            || created.tenant_id != expected_tenant
            || created.template_id != expected_template
            || created.parent_tenant.is_some()
            || created.placement_lock != expected_lock
        {
            return Err(TenantApiError::PortFailure("database"));
        }
        Ok(created)
    }

    /// Gets exactly one tenant/database after authorization.
    ///
    /// # Errors
    ///
    /// Returns scope, authorization, missing-port, or durable errors. An absent or
    /// mismatched database returns `NotFound`.
    pub fn get_tenant(
        &self,
        actor: &TenantActor,
        tenant_id: &TenantId,
    ) -> Result<TenantDatabase, TenantApiError> {
        validate_actor_target(actor, tenant_id)?;
        self.authorize(actor, TenantOperation::ReadTenant, tenant_id)?;
        let database =
            self.database()?.get(tenant_id).map_err(map_port)?.ok_or(TenantApiError::NotFound)?;
        if database.tenant_id != *tenant_id || database.placement_lock.validate().is_err() {
            return Err(TenantApiError::NotFound);
        }
        Ok(database)
    }

    /// Lists databases for one exact tenant owner scope. There is no
    /// cross-tenant or unscoped list method.
    ///
    /// # Errors
    ///
    /// Returns pagination, authorization, missing-port, or durable errors, or a port
    /// failure for an oversized or incorrectly scoped result.
    pub fn list_tenants(
        &self,
        actor: &TenantActor,
        owner_tenant: &TenantId,
        page: &PageRequest,
    ) -> Result<Page<TenantDatabase>, TenantApiError> {
        page.validate()?;
        validate_actor_target(actor, owner_tenant)?;
        self.authorize(actor, TenantOperation::ListTenants, owner_tenant)?;
        let result = self.database()?.list(owner_tenant, page).map_err(map_port)?;
        validate_page(&result, page, |database| {
            if database.owner_tenant != *owner_tenant
                || database.tenant_id.as_str().is_empty()
                || database.template_id.as_str().is_empty()
                || database.placement_lock.validate().is_err()
            {
                return Err(TenantApiError::PortFailure("database"));
            }
            Ok(())
        })?;
        Ok(result)
    }

    /// Deletes exactly one tenant/database. The durable port owns its
    /// deletion semantics (soft delete or purge) and idempotent replay.
    ///
    /// # Errors
    ///
    /// Returns scope, authorization, missing-port, or durable deletion errors.
    pub fn delete_tenant(
        &self,
        actor: &TenantActor,
        tenant_id: &TenantId,
        idempotency_key: &IdempotencyKey,
    ) -> Result<(), TenantApiError> {
        validate_actor_target(actor, tenant_id)?;
        self.authorize(actor, TenantOperation::DeleteTenant, tenant_id)?;
        self.database()?.delete(tenant_id, idempotency_key).map_err(map_port)
    }

    /// Creates a support branch under the source tenant's immutable lock.
    ///
    /// # Errors
    ///
    /// Returns scope, authorization, missing-source, placement, branch-identity, or
    /// durable errors. A mismatched created branch returns a database-port failure.
    pub fn create_branch(
        &self,
        actor: &TenantActor,
        source_tenant: &TenantId,
        request: CreateTenantBranch,
        idempotency_key: IdempotencyKey,
    ) -> Result<TenantDatabase, TenantApiError> {
        validate_actor_target(actor, source_tenant)?;
        self.authorize(actor, TenantOperation::CreateBranch, source_tenant)?;
        let source = self
            .database()?
            .get(source_tenant)
            .map_err(map_port)?
            .ok_or(TenantApiError::NotFound)?;
        if source.tenant_id != *source_tenant {
            return Err(TenantApiError::NotFound);
        }
        source.placement_lock.validate().map_err(|_| TenantApiError::PortFailure("database"))?;
        let lock = source.placement_lock.child(request.placement_lock.as_ref())?;
        let expected_branch = request.branch_tenant_id.clone();
        if expected_branch == *source_tenant {
            return Err(TenantApiError::Conflict);
        }
        let expected_template = source.template_id.clone();
        let expected_owner = source.owner_tenant.clone();
        let expected_lock = lock.clone();
        let branch = TenantDatabase {
            owner_tenant: source.owner_tenant.clone(),
            tenant_id: request.branch_tenant_id,
            template_id: source.template_id.clone(),
            placement_lock: lock,
            parent_tenant: Some(source.tenant_id),
        };
        let created = self
            .database()?
            .create_branch(&BranchCreateCommand {
                source_tenant: source_tenant.clone(),
                branch,
                idempotency_key,
            })
            .map_err(map_port)?;
        if created.owner_tenant != expected_owner
            || created.tenant_id != expected_branch
            || created.template_id != expected_template
            || created.placement_lock != expected_lock
            || created.parent_tenant.as_ref() != Some(source_tenant)
        {
            return Err(TenantApiError::PortFailure("database"));
        }
        Ok(created)
    }

    /// Mints a tenant-scoped key using the database's inherited key policy.
    /// The service checks the adapter's returned hash before exposing the
    /// plaintext result, ensuring a misbehaving adapter cannot claim it
    /// stored a secret hash when it did not.
    /// Branches may narrow the template's placement while retaining its residency.
    ///
    /// # Errors
    ///
    /// Returns scope or authorization failures, missing adapters or records,
    /// invalid inherited placement or policy, provider errors, or invalid key material.
    pub fn mint_tenant_key(
        &self,
        actor: &TenantActor,
        tenant_id: &TenantId,
        idempotency_key: IdempotencyKey,
    ) -> Result<TenantApiKey, TenantApiError> {
        validate_actor_target(actor, tenant_id)?;
        self.authorize(actor, TenantOperation::MintKey, tenant_id)?;
        let database =
            self.database()?.get(tenant_id).map_err(map_port)?.ok_or(TenantApiError::NotFound)?;
        if database.tenant_id != *tenant_id || database.placement_lock.validate().is_err() {
            return Err(TenantApiError::NotFound);
        }
        let template = self
            .control()?
            .get_template(&database.owner_tenant, &database.template_id)
            .map_err(map_port)?
            .ok_or(TenantApiError::NotFound)?;
        if template.owner_tenant != database.owner_tenant
            || template.id != database.template_id
            || if database.parent_tenant.is_some() {
                template.placement_lock.child(Some(&database.placement_lock)).is_err()
            } else {
                template.placement_lock != database.placement_lock
            }
            || template.placement_lock.validate().is_err()
            || template.key_policy.validate().is_err()
        {
            return Err(TenantApiError::NotFound);
        }
        let minted = self
            .keys()?
            .mint(&KeyMintCommand {
                tenant_id: tenant_id.clone(),
                policy: template.key_policy,
                idempotency_key,
            })
            .map_err(map_port)?;
        if minted.secret.len() < 16
            || minted.secret.len() > MAX_KEY_SECRET_BYTES
            || !minted.secret.is_ascii()
            || minted
                .secret
                .bytes()
                .any(|byte| byte.is_ascii_whitespace() || byte.is_ascii_control())
            || blake3::hash(minted.secret.as_bytes()).as_bytes() != &minted.secret_hash
        {
            return Err(TenantApiError::KeyMaterialInvalid);
        }
        if minted.id.as_str().is_empty() {
            return Err(TenantApiError::KeyMaterialInvalid);
        }
        Ok(TenantApiKey { id: minted.id, secret: minted.secret })
    }

    /// Reads usage facts for exactly one tenant/database.
    ///
    /// # Errors
    ///
    /// Returns scope, authorization, missing-port, or durable errors, or a usage-port
    /// failure for mismatched tenant identity or an invalid time window.
    pub fn tenant_usage(
        &self,
        actor: &TenantActor,
        tenant_id: &TenantId,
    ) -> Result<TenantUsage, TenantApiError> {
        validate_actor_target(actor, tenant_id)?;
        self.authorize(actor, TenantOperation::ReadUsage, tenant_id)?;
        let usage = self.usage()?.get(tenant_id).map_err(map_port)?;
        if usage.tenant_id != *tenant_id || usage.window_end_millis < usage.window_start_millis {
            return Err(TenantApiError::PortFailure("usage"));
        }
        Ok(usage)
    }

    fn authorize(
        &self,
        actor: &TenantActor,
        operation: TenantOperation,
        target: &TenantId,
    ) -> Result<(), TenantApiError> {
        self.authorization()?.authorize(actor, operation, target).map_err(|error| match error {
            AuthorizationError::Unauthorized => TenantApiError::Unauthorized,
            AuthorizationError::Forbidden => TenantApiError::Forbidden,
            AuthorizationError::Unavailable => TenantApiError::AdapterUnavailable("authorization"),
        })
    }

    fn database(&self) -> Result<&dyn DatabasePort, TenantApiError> {
        self.ports.database.as_deref().ok_or(TenantApiError::AdapterUnavailable("database"))
    }

    fn control(&self) -> Result<&dyn ControlPort, TenantApiError> {
        self.ports.control.as_deref().ok_or(TenantApiError::AdapterUnavailable("control"))
    }

    fn keys(&self) -> Result<&dyn ApiKeyPort, TenantApiError> {
        self.ports.keys.as_deref().ok_or(TenantApiError::AdapterUnavailable("key"))
    }

    fn usage(&self) -> Result<&dyn UsagePort, TenantApiError> {
        self.ports.usage.as_deref().ok_or(TenantApiError::AdapterUnavailable("usage"))
    }

    fn authorization(&self) -> Result<&dyn AuthorizationPort, TenantApiError> {
        self.ports
            .authorization
            .as_deref()
            .ok_or(TenantApiError::AdapterUnavailable("authorization"))
    }
}

fn validate_actor_target(actor: &TenantActor, target: &TenantId) -> Result<(), TenantApiError> {
    if actor.tenant_id != *target {
        return Err(TenantApiError::Forbidden);
    }
    Ok(())
}

fn map_port(error: PortError) -> TenantApiError {
    match error {
        PortError::NotFound => TenantApiError::NotFound,
        PortError::Conflict => TenantApiError::Conflict,
        PortError::Unavailable => TenantApiError::AdapterUnavailable("durable"),
        PortError::Invalid => TenantApiError::InvalidInput("durable_request"),
        PortError::Failed => TenantApiError::PortFailure("durable"),
    }
}

/// Verifies the shape of a page before it crosses the service boundary.
/// Durable adapters own ordering and cursor encoding, but they must not be
/// able to exceed the caller's bound or return the same cursor forever.
fn validate_page<T>(
    page: &Page<T>,
    request: &PageRequest,
    mut validate_item: impl FnMut(&T) -> Result<(), TenantApiError>,
) -> Result<(), TenantApiError> {
    if page.items.len() > usize::from(request.limit)
        || page.next_cursor.as_ref().is_some_and(|cursor| {
            request.cursor.as_ref().is_some_and(|previous| previous == cursor)
        })
    {
        return Err(TenantApiError::PortFailure("durable"));
    }
    for item in &page.items {
        validate_item(item)?;
    }
    Ok(())
}

/// One proposed parent-router/OpenAPI operation. This list is descriptive;
/// it does not install routes or alter the existing HTTP router.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct TenantRoute {
    /// HTTP method.
    pub method: &'static str,
    /// Exact proposed path.
    pub path: &'static str,
    /// Stable operation identifier for OpenAPI/codegen.
    pub operation_id: &'static str,
}

/// Proposed D-51 HTTP mapping for parent wiring. Every mutating operation
/// requires `Idempotency-Key`; tenant paths remain explicitly scoped.
pub const TENANT_ROUTES: &[TenantRoute] = &[
    TenantRoute {
        method: "POST",
        path: "/api/v1/tenants/templates",
        operation_id: "createTenantTemplate",
    },
    TenantRoute {
        method: "GET",
        path: "/api/v1/tenants/templates/{ownerTenant}/{templateId}",
        operation_id: "getTenantTemplate",
    },
    TenantRoute {
        method: "GET",
        path: "/api/v1/tenants/templates/{ownerTenant}",
        operation_id: "listTenantTemplates",
    },
    TenantRoute {
        method: "DELETE",
        path: "/api/v1/tenants/templates/{ownerTenant}/{templateId}",
        operation_id: "deleteTenantTemplate",
    },
    TenantRoute {
        method: "POST",
        path: "/api/v1/tenants/{tenantId}",
        operation_id: "createTenant",
    },
    TenantRoute { method: "GET", path: "/api/v1/tenants/{tenantId}", operation_id: "getTenant" },
    TenantRoute {
        method: "GET",
        path: "/api/v1/tenants/{ownerTenant}/databases",
        operation_id: "listTenants",
    },
    TenantRoute {
        method: "DELETE",
        path: "/api/v1/tenants/{tenantId}",
        operation_id: "deleteTenant",
    },
    TenantRoute {
        method: "POST",
        path: "/api/v1/tenants/{tenantId}/branches",
        operation_id: "createTenantBranch",
    },
    TenantRoute {
        method: "POST",
        path: "/api/v1/tenants/{tenantId}/keys",
        operation_id: "mintTenantKey",
    },
    TenantRoute {
        method: "GET",
        path: "/api/v1/tenants/{tenantId}/usage",
        operation_id: "getTenantUsage",
    },
];

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Mutex;

    fn id(text: &str) -> Identifier {
        Identifier::try_new(text).expect("valid id")
    }

    fn actor(tenant: &str) -> TenantActor {
        TenantActor { principal: id("principal"), tenant_id: id(tenant) }
    }

    fn lock() -> PlacementLock {
        PlacementLock { placement: vec![RegionCode::GRU, RegionCode::FRA], residency: None }
    }

    fn template() -> TenantTemplate {
        TenantTemplate {
            id: id("default"),
            owner_tenant: id("owner"),
            placement_lock: lock(),
            key_policy: TenantKeyPolicy {
                read: true,
                write: true,
                admin: false,
                expires_in_seconds: 3600,
            },
        }
    }

    struct Allow;
    impl AuthorizationPort for Allow {
        fn authorize(
            &self,
            _: &TenantActor,
            _: TenantOperation,
            _: &TenantId,
        ) -> Result<(), AuthorizationError> {
            Ok(())
        }
    }

    struct Deny;
    impl AuthorizationPort for Deny {
        fn authorize(
            &self,
            _: &TenantActor,
            _: TenantOperation,
            _: &TenantId,
        ) -> Result<(), AuthorizationError> {
            Err(AuthorizationError::Forbidden)
        }
    }

    struct Control {
        corrupt_create: bool,
    }
    impl ControlPort for Control {
        fn create_template(
            &self,
            command: &TemplateCreateCommand,
        ) -> Result<TenantTemplate, PortError> {
            Ok(TenantTemplate {
                id: command.template.id.clone(),
                owner_tenant: if self.corrupt_create {
                    id("other")
                } else {
                    command.template.owner_tenant.clone()
                },
                placement_lock: command.template.placement_lock.clone(),
                key_policy: command.template.key_policy.clone(),
            })
        }
        fn get_template(
            &self,
            _: &TenantId,
            _: &TenantTemplateId,
        ) -> Result<Option<TenantTemplate>, PortError> {
            Ok(Some(template()))
        }
        fn list_templates(
            &self,
            _: &TenantId,
            page: &PageRequest,
        ) -> Result<Page<TenantTemplate>, PortError> {
            assert!(page.limit <= MAX_PAGE_SIZE);
            Ok(Page { items: vec![template()], next_cursor: None })
        }
        fn delete_template(
            &self,
            _: &TenantId,
            _: &TenantTemplateId,
            _: &IdempotencyKey,
        ) -> Result<(), PortError> {
            Ok(())
        }
    }

    struct Database {
        seen: Mutex<Vec<String>>,
        returned: Option<TenantDatabase>,
    }
    impl DatabasePort for Database {
        fn create(&self, command: &DatabaseCreateCommand) -> Result<TenantDatabase, PortError> {
            self.seen.lock().expect("lock").push(command.idempotency_key.as_str().to_owned());
            Ok(command.database.clone())
        }
        fn get(&self, tenant_id: &TenantId) -> Result<Option<TenantDatabase>, PortError> {
            if let Some(database) = &self.returned {
                return Ok(Some(database.clone()));
            }
            Ok(Some(TenantDatabase {
                owner_tenant: id("owner"),
                tenant_id: tenant_id.clone(),
                template_id: id("default"),
                placement_lock: lock(),
                parent_tenant: None,
            }))
        }
        fn list(&self, _: &TenantId, _: &PageRequest) -> Result<Page<TenantDatabase>, PortError> {
            Ok(Page { items: Vec::new(), next_cursor: None })
        }
        fn delete(&self, _: &TenantId, _: &IdempotencyKey) -> Result<(), PortError> {
            Ok(())
        }
        fn create_branch(
            &self,
            command: &BranchCreateCommand,
        ) -> Result<TenantDatabase, PortError> {
            Ok(command.branch.clone())
        }
    }

    struct Keys;
    impl ApiKeyPort for Keys {
        fn mint(&self, _: &KeyMintCommand) -> Result<MintedKey, PortError> {
            let secret = "tskey_secret_for_test".to_owned();
            Ok(MintedKey {
                id: id("key-1"),
                secret: secret.clone(),
                secret_hash: *blake3::hash(secret.as_bytes()).as_bytes(),
            })
        }
    }

    struct Usage;
    impl UsagePort for Usage {
        fn get(&self, tenant_id: &TenantId) -> Result<TenantUsage, PortError> {
            Ok(TenantUsage {
                tenant_id: tenant_id.clone(),
                window_start_millis: 1,
                window_end_millis: 2,
                writes: 3,
                storage_bytes: 4,
            })
        }
    }

    fn service() -> TenantService {
        TenantService::new(TenantPorts {
            database: Some(Arc::new(Database { seen: Mutex::new(Vec::new()), returned: None })),
            control: Some(Arc::new(Control { corrupt_create: false })),
            keys: Some(Arc::new(Keys)),
            usage: Some(Arc::new(Usage)),
            authorization: Some(Arc::new(Allow)),
        })
    }

    #[test]
    fn absent_adapters_fail_closed_without_a_local_tenant() {
        let tenant = id("owner");
        let result = TenantService::without_adapters().get_tenant(&actor("owner"), &tenant);
        assert_eq!(result, Err(TenantApiError::AdapterUnavailable("authorization")));
    }

    #[test]
    fn handler_ready_rejects_an_incomplete_production_composition() {
        assert!(matches!(
            TenantService::handler_ready(TenantPorts::default()),
            Err(TenantApiError::AdapterUnavailable("authorization"))
        ));
    }

    #[test]
    fn authorization_precedes_database_lookup_and_blocks_cross_tenant_access() {
        let service = TenantService::new(TenantPorts {
            authorization: Some(Arc::new(Deny)),
            ..TenantPorts::default()
        });
        let result = service.get_tenant(&actor("owner"), &id("other"));
        assert_eq!(result, Err(TenantApiError::Forbidden));
    }

    #[test]
    fn create_inherits_template_lock_and_forwards_idempotency() {
        let service = service();
        let result = service
            .create_tenant(
                &actor("owner"),
                CreateTenant {
                    owner_tenant: id("owner"),
                    tenant_id: id("child"),
                    template_id: id("default"),
                },
                IdempotencyKey::try_new("create-1").expect("key"),
            )
            .expect("create");
        assert_eq!(result.placement_lock, lock());
    }

    #[test]
    fn adapter_cannot_return_a_template_for_another_owner() {
        let service = TenantService::new(TenantPorts {
            control: Some(Arc::new(Control { corrupt_create: true })),
            authorization: Some(Arc::new(Allow)),
            ..TenantPorts::default()
        });
        let template = CreateTenantTemplate {
            id: id("default"),
            owner_tenant: id("owner"),
            placement_lock: lock(),
            key_policy: TenantKeyPolicy {
                read: true,
                write: true,
                admin: false,
                expires_in_seconds: 3600,
            },
        };
        let result = service.create_template(
            &actor("owner"),
            &TemplateCreateCommand {
                template,
                idempotency_key: IdempotencyKey::try_new("template-1").expect("key"),
            },
        );
        assert_eq!(result, Err(TenantApiError::PortFailure("control")));
    }

    #[test]
    fn branch_cannot_widen_or_change_immutable_lock() {
        let service = service();
        let result = service.create_branch(
            &actor("owner"),
            &id("owner"),
            CreateTenantBranch {
                branch_tenant_id: id("branch"),
                placement_lock: Some(PlacementLock {
                    placement: vec![RegionCode::SFO],
                    residency: None,
                }),
            },
            IdempotencyKey::try_new("branch-1").expect("key"),
        );
        assert_eq!(result, Err(TenantApiError::PlacementLockViolation));
    }

    #[test]
    fn branch_keys_accept_narrowed_placement_but_refuse_widening_or_residency_changes() {
        for (parent, region, residency, allowed) in [
            (Some(id("source")), RegionCode::GRU, None, true),
            (None, RegionCode::GRU, None, false),
            (Some(id("source")), RegionCode::SFO, None, false),
            (Some(id("source")), RegionCode::GRU, Some("changed".to_owned()), false),
        ] {
            let mut service = service();
            service.ports.database = Some(Arc::new(Database {
                seen: Mutex::new(Vec::new()),
                returned: Some(TenantDatabase {
                    owner_tenant: id("owner"),
                    tenant_id: id("branch"),
                    template_id: id("default"),
                    placement_lock: PlacementLock { placement: vec![region], residency },
                    parent_tenant: parent,
                }),
            }));
            let result = service.mint_tenant_key(
                &actor("branch"),
                &id("branch"),
                IdempotencyKey::try_new("key-1").expect("key"),
            );
            if allowed {
                assert_eq!(result.expect("valid narrowed branch").secret, "tskey_secret_for_test");
            } else {
                assert_eq!(result, Err(TenantApiError::NotFound));
            }
        }
    }

    #[test]
    fn key_is_returned_once_only_after_hash_verification() {
        let result = service()
            .mint_tenant_key(
                &actor("owner"),
                &id("owner"),
                IdempotencyKey::try_new("key-1").expect("key"),
            )
            .expect("mint");
        assert_eq!(result.secret, "tskey_secret_for_test");
    }

    #[test]
    fn pagination_is_bounded_and_routes_are_descriptive_only() {
        assert_eq!(PageRequest::try_new(0, None), Err(TenantApiError::InvalidInput("page_size")));
        assert!(TENANT_ROUTES.iter().any(|route| route.operation_id == "mintTenantKey"));
        assert!(!TENANT_ROUTES.iter().any(|route| route.path == "/api/v1/tenants"));
    }
}
