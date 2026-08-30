//! Bounded, versioned authorization evaluation for the private engine.
//!
//! This module deliberately starts after authentication.  A trusted gateway
//! supplies `VerifiedPrincipalInput` through the crate-private construction
//! seam; the engine never parses public claims, passwords, tokens, or keys.

use std::{
    collections::{BTreeMap, BTreeSet},
    fmt,
};

use crate::identity::{
    AuthorizationPolicyUlid, DatabaseUlid, PrincipalGrantUlid, PrincipalSessionUlid, PrincipalUlid,
    ProjectUlid, TenantUlid,
};

mod signed;

pub use signed::{
    AuthorizationCryptoMode, PrincipalContextEnvelope, PrincipalContextKey,
    PrincipalContextKeySet, PrincipalContextPayload, PrincipalContextReplayGuard,
    PrincipalContextVerifier, SigningKeyProvenance,
};

/// The canonical version of the engine authorization policy language.
pub const AUTHORIZATION_FORMAT_VERSION: u16 = 1;
/// The largest number of grants in one policy.
pub const MAX_POLICY_RULES: usize = 256;
/// The largest number of claim predicates in one grant.
pub const MAX_RULE_PREDICATES: usize = 64;
/// The largest number of indexed fields declared by one policy.
pub const MAX_INDEXED_FIELDS: usize = 512;
/// The largest number of actions in one verified principal context.
pub const MAX_PRINCIPAL_ACTIONS: usize = 32;
/// The largest number of claims in one verified principal context.
pub const MAX_PRINCIPAL_CLAIMS: usize = 128;
/// The largest relationship traversal depth supported by a policy placeholder.
pub const MAX_RELATIONSHIP_HOPS: u8 = 4;
/// The largest relationship result bound supported by a policy placeholder.
pub const MAX_RELATIONSHIP_MATCHES: u32 = 1_024;
const MAX_IDENTIFIER_BYTES: usize = 255;

/// A typed failure while publishing or constructing authorization state.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum AuthorizationError {
    /// A required identifier or audience was empty, oversized, or malformed.
    InvalidIdentifier,
    /// The authorization language version is not supported.
    UnsupportedVersion,
    /// A policy generation of zero is not a valid generation.
    InvalidGeneration,
    /// An expiry was not strictly after the issuance time.
    InvalidExpiry,
    /// A policy exceeded a fixed cardinality bound.
    ExceedsBound(&'static str),
    /// A policy predicate references a field without a declared index.
    UnindexedPredicate(String),
    /// A relationship predicate omitted a finite traversal or result bound.
    UnboundedRelationship,
    /// A policy contained two identical canonical grants.
    DuplicateRule,
    /// The signed context envelope is malformed or exceeds a bound.
    MalformedContextEnvelope,
    /// The signed context payload is not canonical or contains an invalid value.
    InvalidContextPayload,
    /// The envelope names no trusted verification key.
    UnknownContextKey,
    /// Signature verification failed.
    InvalidContextSignature,
    /// The trusted key set contains a duplicate key identifier.
    DuplicateContextKey,
    /// The signed context is outside its validity interval.
    ContextNotYetValid,
    /// The signed context has expired.
    ContextExpired,
    /// The signed context does not match the verifier's binding.
    ContextBindingMismatch,
    /// The context grant was already accepted on this channel binding.
    ContextReplay,
    /// The bounded context replay state cannot track another grant.
    ContextReplayGuardFull,
    /// A local-development signing key was used for production verification.
    DevelopmentSigningKey,
    /// Signature creation failed.
    ContextSigningFailed,
}

impl fmt::Display for AuthorizationError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::InvalidIdentifier => formatter.write_str("invalid authorization identifier"),
            Self::UnsupportedVersion => formatter.write_str("unsupported authorization version"),
            Self::InvalidGeneration => formatter.write_str("invalid authorization generation"),
            Self::InvalidExpiry => formatter.write_str("invalid principal expiry"),
            Self::ExceedsBound(name) => write!(formatter, "authorization bound exceeded: {name}"),
            Self::UnindexedPredicate(field) => {
                write!(formatter, "authorization predicate is not indexed: {field}")
            }
            Self::UnboundedRelationship => {
                formatter.write_str("authorization relationship is unbounded")
            }
            Self::DuplicateRule => {
                formatter.write_str("authorization policy contains a duplicate rule")
            }
            Self::MalformedContextEnvelope => formatter.write_str("malformed principal context envelope"),
            Self::InvalidContextPayload => formatter.write_str("invalid principal context payload"),
            Self::UnknownContextKey => formatter.write_str("unknown principal context key"),
            Self::InvalidContextSignature => formatter.write_str("invalid principal context signature"),
            Self::DuplicateContextKey => formatter.write_str("duplicate principal context key"),
            Self::ContextNotYetValid => formatter.write_str("principal context is not yet valid"),
            Self::ContextExpired => formatter.write_str("principal context has expired"),
            Self::ContextBindingMismatch => formatter.write_str("principal context binding mismatch"),
            Self::ContextReplay => formatter.write_str("principal context replay refused"),
            Self::ContextReplayGuardFull => formatter.write_str("principal context replay guard is full"),
            Self::DevelopmentSigningKey => formatter.write_str("development signing key rejected in production"),
            Self::ContextSigningFailed => formatter.write_str("principal context signing failed"),
        }
    }
}

impl std::error::Error for AuthorizationError {}

fn valid_identifier(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= MAX_IDENTIFIER_BYTES
        && value == value.trim()
        && !value.chars().any(char::is_control)
}

fn valid_field_path(value: &str) -> bool {
    valid_identifier(value)
        && value != "*"
        && value.split('.').all(|part| !part.is_empty() && valid_identifier(part))
}

fn valid_scope_identifier(value: &str) -> bool {
    valid_identifier(value) && value != "*"
}

/// A bounded action that a principal may perform.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub enum Action {
    /// Read one or more documents.
    Read,
    /// Execute a bounded query.
    Query,
    /// Mutate documents.
    Mutate,
    /// Open or resume a reactive subscription.
    Subscribe,
    /// Change schema metadata.
    Schema,
    /// Perform an explicitly granted administration operation.
    Admin,
}

/// A policy operation.  It is separate from [`Action`] so policy ASTs do not
/// accidentally become an admission grant.
#[derive(Clone, Copy, Debug, Eq, Hash, Ord, PartialEq, PartialOrd)]
pub enum Operation {
    /// Match a document read.
    Read,
    /// Match a query.
    Query,
    /// Match a document mutation.
    Mutate,
    /// Match a reactive subscription.
    Subscribe,
    /// Match a schema operation.
    Schema,
    /// Match an administration operation.
    Admin,
}

impl Operation {
    fn tag(self) -> &'static str {
        match self {
            Self::Read => "read",
            Self::Query => "query",
            Self::Mutate => "mutate",
            Self::Subscribe => "subscribe",
            Self::Schema => "schema",
            Self::Admin => "admin",
        }
    }
}

impl From<Action> for Operation {
    fn from(action: Action) -> Self {
        match action {
            Action::Read => Self::Read,
            Action::Query => Self::Query,
            Action::Mutate => Self::Mutate,
            Action::Subscribe => Self::Subscribe,
            Action::Schema => Self::Schema,
            Action::Admin => Self::Admin,
        }
    }
}

/// A scalar value used by signed principal claims and indexed document fields.
#[derive(Clone, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub enum AttributeValue {
    /// A boolean value.
    Boolean(bool),
    /// A signed integer value.
    Integer(i64),
    /// A UTF-8 string value.
    String(String),
}

/// A bounded set of claims carried by a verified principal.
#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct PrincipalClaims(BTreeMap<String, AttributeValue>);

impl PrincipalClaims {
    /// Builds claims after enforcing name and cardinality bounds.
    ///
    /// # Errors
    ///
    /// Returns [`AuthorizationError::ExceedsBound`] when too many claims are
    /// supplied, or [`AuthorizationError::InvalidIdentifier`] for a malformed
    /// claim name.
    pub fn try_new<I, K>(claims: I) -> Result<Self, AuthorizationError>
    where
        I: IntoIterator<Item = (K, AttributeValue)>,
        K: Into<String>,
    {
        let map: BTreeMap<String, AttributeValue> =
            claims.into_iter().map(|(key, value)| (key.into(), value)).collect();
        if map.len() > MAX_PRINCIPAL_CLAIMS {
            return Err(AuthorizationError::ExceedsBound("principal claims"));
        }
        if map.keys().any(|key| !valid_identifier(key)) {
            return Err(AuthorizationError::InvalidIdentifier);
        }
        Ok(Self(map))
    }

    /// Returns the number of claims.
    #[must_use]
    pub fn len(&self) -> usize {
        self.0.len()
    }

    /// Returns whether no claims are present.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.0.is_empty()
    }
}

/// A tenant, Project, and database binding used by both context and request.
#[derive(Clone, Debug, Eq, Ord, PartialEq, PartialOrd)]
#[allow(
    clippy::struct_field_names,
    reason = "scope fields intentionally mirror the three engine bindings"
)]
pub struct ResourceScope {
    tenant_id: TenantUlid,
    project_id: ProjectUlid,
    database_id: DatabaseUlid,
}

impl ResourceScope {
    /// Creates a validated resource scope.
    ///
    /// # Errors
    ///
    /// Returns [`AuthorizationError::InvalidIdentifier`] when any scope
    /// component is empty, malformed, or exceeds the identifier bound.
    pub fn try_new(
        tenant_id: TenantUlid,
        project_id: ProjectUlid,
        database_id: DatabaseUlid,
    ) -> Result<Self, AuthorizationError> {
        Ok(Self { tenant_id, project_id, database_id })
    }

    /// Returns the tenant identity.
    #[must_use]
    pub const fn tenant_id(&self) -> TenantUlid {
        self.tenant_id
    }

    /// Returns the Project identity.
    #[must_use]
    pub const fn project_id(&self) -> ProjectUlid {
        self.project_id
    }

    /// Returns the database identity.
    #[must_use]
    pub const fn database_id(&self) -> DatabaseUlid {
        self.database_id
    }
}

/// The verified, non-public input from the Database API trust boundary.
///
/// The type and fields are crate-private intentionally.  A caller must use a
/// verifier-owned seam to create it; this module does not parse or verify
/// claims, tokens, passwords, signatures, or cryptographic keys.
#[derive(Clone, Debug)]
#[cfg_attr(
    not(test),
    expect(
        dead_code,
        reason = "the verified gateway seam is consumed when transport integration lands"
    )
)]
pub(crate) struct VerifiedPrincipalInput {
    pub(crate) scope: ResourceScope,
    pub(crate) principal_id: String,
    pub(crate) actions: BTreeSet<Action>,
    pub(crate) claims: PrincipalClaims,
    pub(crate) audience: String,
    pub(crate) issued_at: u64,
    pub(crate) expires_at: u64,
    pub(crate) policy_generation: u64,
}

/// An opaque engine authorization context produced only by the verified seam.
#[derive(Clone)]
pub struct PrincipalContext {
    scope: ResourceScope,
    principal_id: String,
    actions: BTreeSet<Action>,
    claims: PrincipalClaims,
    audience: String,
    issued_at: u64,
    expires_at: u64,
    policy_generation: u64,
}

impl fmt::Debug for PrincipalContext {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.debug_struct("PrincipalContext").finish_non_exhaustive()
    }
}

impl PrincipalContext {
    /// Constructs an opaque context from already verified gateway input.
    #[cfg_attr(
        not(test),
        expect(
            dead_code,
            reason = "the verified gateway seam is consumed when transport integration lands"
        )
    )]
    pub(crate) fn from_verified(input: VerifiedPrincipalInput) -> Result<Self, AuthorizationError> {
        if !valid_scope_identifier(&input.principal_id) || !valid_scope_identifier(&input.audience)
        {
            return Err(AuthorizationError::InvalidIdentifier);
        }
        if input.actions.is_empty() || input.actions.len() > MAX_PRINCIPAL_ACTIONS {
            return Err(AuthorizationError::ExceedsBound("principal actions"));
        }
        if input.expires_at <= input.issued_at {
            return Err(AuthorizationError::InvalidExpiry);
        }
        if input.policy_generation == 0 {
            return Err(AuthorizationError::InvalidGeneration);
        }
        Ok(Self {
            scope: input.scope,
            principal_id: input.principal_id,
            actions: input.actions,
            claims: input.claims,
            audience: input.audience,
            issued_at: input.issued_at,
            expires_at: input.expires_at,
            policy_generation: input.policy_generation,
        })
    }

    /// Returns the tenant binding without exposing claim material.
    #[must_use]
    pub fn tenant_id(&self) -> &str {
        self.scope.tenant_id()
    }

    /// Returns the Project binding without exposing claim material.
    #[must_use]
    pub fn project_id(&self) -> &str {
        self.scope.project_id()
    }

    /// Returns the database binding without exposing claim material.
    #[must_use]
    pub fn database_id(&self) -> &str {
        self.scope.database_id()
    }

    /// Returns the opaque principal identity.
    #[must_use]
    pub fn principal_id(&self) -> &str {
        &self.principal_id
    }

    /// Returns the audience binding.
    #[must_use]
    pub fn audience(&self) -> &str {
        &self.audience
    }

    /// Returns the policy generation bound to this context.
    #[must_use]
    pub const fn policy_generation(&self) -> u64 {
        self.policy_generation
    }

    /// Returns the expiry instant in Unix seconds.
    #[must_use]
    pub const fn expires_at(&self) -> u64 {
        self.expires_at
    }
}

/// A comparison performed against one indexed document field and one claim.
#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub enum ClaimComparison {
    /// Values must be equal.
    Equals,
    /// Values must differ.
    NotEquals,
    /// The document value must sort before the claim value.
    LessThan,
    /// The document value must sort at or before the claim value.
    LessOrEqual,
    /// The document value must sort after the claim value.
    GreaterThan,
    /// The document value must sort at or after the claim value.
    GreaterOrEqual,
}

/// An indexed claim predicate in the policy AST.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ClaimPredicate {
    field: String,
    claim: String,
    comparison: ClaimComparison,
}

impl ClaimPredicate {
    /// Creates a predicate.  Publication validates that `field` is indexed.
    ///
    /// # Errors
    ///
    /// Returns [`AuthorizationError::InvalidIdentifier`] when the field path
    /// or claim name is malformed.
    pub fn try_new(
        field: &str,
        claim: &str,
        comparison: ClaimComparison,
    ) -> Result<Self, AuthorizationError> {
        if !valid_field_path(field) || !valid_identifier(claim) {
            return Err(AuthorizationError::InvalidIdentifier);
        }
        Ok(Self { field: field.to_owned(), claim: claim.to_owned(), comparison })
    }

    /// Returns the indexed document field.
    #[must_use]
    pub fn field(&self) -> &str {
        &self.field
    }

    /// Returns the principal claim name.
    #[must_use]
    pub fn claim(&self) -> &str {
        &self.claim
    }
}

/// A finite relationship check placeholder.  Resolution is supplied as
/// bounded evidence by a future relationship index; no graph scan is allowed.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RelationshipPredicate {
    relation: String,
    target_collection: String,
    max_hops: u8,
    max_matches: u32,
}

impl RelationshipPredicate {
    /// Creates a finite relationship placeholder.
    ///
    /// # Errors
    ///
    /// Returns [`AuthorizationError::InvalidIdentifier`] for malformed names,
    /// or [`AuthorizationError::UnboundedRelationship`] when either bound is
    /// zero or exceeds the supported limit.
    pub fn try_new(
        relation: &str,
        target_collection: &str,
        max_hops: u8,
        max_matches: u32,
    ) -> Result<Self, AuthorizationError> {
        if !valid_identifier(relation) || !valid_identifier(target_collection) {
            return Err(AuthorizationError::InvalidIdentifier);
        }
        if max_hops == 0
            || max_hops > MAX_RELATIONSHIP_HOPS
            || max_matches == 0
            || max_matches > MAX_RELATIONSHIP_MATCHES
        {
            return Err(AuthorizationError::UnboundedRelationship);
        }
        Ok(Self {
            relation: relation.to_owned(),
            target_collection: target_collection.to_owned(),
            max_hops,
            max_matches,
        })
    }

    /// Returns the relationship name.
    #[must_use]
    pub fn relation(&self) -> &str {
        &self.relation
    }

    /// Returns the target collection.
    #[must_use]
    pub fn target_collection(&self) -> &str {
        &self.target_collection
    }

    /// Returns the maximum traversal depth.
    #[must_use]
    pub const fn max_hops(&self) -> u8 {
        self.max_hops
    }

    /// Returns the maximum number of matching edges.
    #[must_use]
    pub const fn max_matches(&self) -> u32 {
        self.max_matches
    }

    fn key(&self) -> String {
        format!(
            "{}:{}:{}:{}",
            self.relation, self.target_collection, self.max_hops, self.max_matches
        )
    }
}

/// Field-level allow and deny projection attached to a grant.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct FieldProjection {
    allow: Option<BTreeSet<String>>,
    deny: BTreeSet<String>,
}

impl FieldProjection {
    /// Returns a projection that initially permits every field.
    #[must_use]
    pub fn all() -> Self {
        Self { allow: None, deny: BTreeSet::new() }
    }

    /// Returns a projection that initially permits no fields.
    #[must_use]
    pub fn none() -> Self {
        Self { allow: Some(BTreeSet::new()), deny: BTreeSet::new() }
    }

    /// Creates an allow-list projection from field paths.
    ///
    /// # Errors
    ///
    /// Returns [`AuthorizationError::InvalidIdentifier`] for malformed field
    /// paths, or [`AuthorizationError::ExceedsBound`] when the list is too
    /// large.
    pub fn try_allow<I, S>(fields: I) -> Result<Self, AuthorizationError>
    where
        I: IntoIterator<Item = S>,
        S: Into<String>,
    {
        let allow = fields.into_iter().map(Into::into).collect::<BTreeSet<_>>();
        if allow.iter().any(|field| !valid_field_path(field)) {
            return Err(AuthorizationError::InvalidIdentifier);
        }
        if allow.len() > MAX_INDEXED_FIELDS {
            return Err(AuthorizationError::ExceedsBound("projection fields"));
        }
        Ok(Self { allow: Some(allow), deny: BTreeSet::new() })
    }

    /// Adds a deny-list to this projection and returns the resulting value.
    ///
    /// # Errors
    ///
    /// Returns [`AuthorizationError::InvalidIdentifier`] for malformed field
    /// paths, or [`AuthorizationError::ExceedsBound`] when the list is too
    /// large.
    pub fn try_deny<I, S>(mut self, fields: I) -> Result<Self, AuthorizationError>
    where
        I: IntoIterator<Item = S>,
        S: Into<String>,
    {
        for field in fields.into_iter().map(Into::into) {
            if !valid_field_path(&field) {
                return Err(AuthorizationError::InvalidIdentifier);
            }
            self.deny.insert(field);
        }
        if self.deny.len() > MAX_INDEXED_FIELDS {
            return Err(AuthorizationError::ExceedsBound("projection fields"));
        }
        Ok(self)
    }

    /// Applies allow then deny semantics to a document field map.
    #[must_use]
    pub fn apply(
        &self,
        fields: &BTreeMap<String, AttributeValue>,
    ) -> BTreeMap<String, AttributeValue> {
        fields
            .iter()
            .filter(|(field, _)| {
                self.allow.as_ref().is_none_or(|allow| allow.contains(*field))
                    && !self.deny.contains(*field)
            })
            .map(|(field, value)| (field.clone(), value.clone()))
            .collect()
    }

    fn canonical_key(&self) -> String {
        let allow = self.allow.as_ref().map_or_else(
            || "*".to_owned(),
            |fields| fields.iter().cloned().collect::<Vec<_>>().join(","),
        );
        format!("a={allow};d={}", self.deny.iter().cloned().collect::<Vec<_>>().join(","))
    }
}

/// One deterministic operation and collection grant.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct PolicyRule {
    operation: Operation,
    collection: String,
    claim_predicates: Vec<ClaimPredicate>,
    relationship: Option<RelationshipPredicate>,
    projection: FieldProjection,
}

impl PolicyRule {
    /// Creates a grant for one operation and exact collection, or `*` for all
    /// collections (still bounded by the other predicates and projection).
    ///
    /// # Errors
    ///
    /// Returns [`AuthorizationError::InvalidIdentifier`] when the collection
    /// selector is malformed.
    pub fn try_new(operation: Operation, collection: &str) -> Result<Self, AuthorizationError> {
        if collection != "*" && !valid_identifier(collection) {
            return Err(AuthorizationError::InvalidIdentifier);
        }
        Ok(Self {
            operation,
            collection: collection.to_owned(),
            claim_predicates: Vec::new(),
            relationship: None,
            projection: FieldProjection::all(),
        })
    }

    /// Adds an indexed claim predicate to this grant.
    #[must_use]
    pub fn with_claim_predicate(mut self, predicate: ClaimPredicate) -> Self {
        self.claim_predicates.push(predicate);
        self
    }

    /// Adds a finite relationship placeholder to this grant.
    #[must_use]
    pub fn with_relationship(mut self, relationship: RelationshipPredicate) -> Self {
        self.relationship = Some(relationship);
        self
    }

    /// Replaces the field projection for this grant.
    #[must_use]
    pub fn with_projection(mut self, projection: FieldProjection) -> Self {
        self.projection = projection;
        self
    }

    /// Returns the operation.
    #[must_use]
    pub const fn operation(&self) -> Operation {
        self.operation
    }

    /// Returns the collection selector.
    #[must_use]
    pub fn collection(&self) -> &str {
        &self.collection
    }

    fn canonical_key(&self) -> String {
        let mut predicates = self
            .claim_predicates
            .iter()
            .map(|predicate| {
                format!("{}:{}:{:?}", predicate.field, predicate.claim, predicate.comparison)
            })
            .collect::<Vec<_>>();
        predicates.sort_unstable();
        let predicates = predicates.join(",");
        let relation =
            self.relationship.as_ref().map_or_else(String::new, RelationshipPredicate::key);
        format!(
            "{}|{}|{}|{}|{}",
            self.operation.tag(),
            self.collection,
            predicates,
            relation,
            self.projection.canonical_key()
        )
    }
}

/// A validated, versioned, deterministic authorization policy AST.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Policy {
    version: u16,
    generation: u64,
    indexed_fields: BTreeSet<String>,
    rules: Vec<PolicyRule>,
}

impl Policy {
    /// Publishes a policy after checking every bounded and indexed predicate.
    ///
    /// # Errors
    ///
    /// Returns a typed [`AuthorizationError`] when the generation is invalid,
    /// a cardinality bound is exceeded, a predicate is unindexed, a
    /// relationship is unbounded, or a duplicate grant is present.
    pub fn try_new<I, S>(
        generation: u64,
        indexed_fields: I,
        rules: impl IntoIterator<Item = PolicyRule>,
    ) -> Result<Self, AuthorizationError>
    where
        I: IntoIterator<Item = S>,
        S: Into<String>,
    {
        if generation == 0 {
            return Err(AuthorizationError::InvalidGeneration);
        }
        let indexed_fields = indexed_fields.into_iter().map(Into::into).collect::<BTreeSet<_>>();
        if indexed_fields.len() > MAX_INDEXED_FIELDS
            || indexed_fields.iter().any(|field| !valid_field_path(field))
        {
            return Err(AuthorizationError::ExceedsBound("indexed fields"));
        }
        let mut rules = rules.into_iter().collect::<Vec<_>>();
        if rules.len() > MAX_POLICY_RULES {
            return Err(AuthorizationError::ExceedsBound("policy rules"));
        }
        for rule in &rules {
            if rule.claim_predicates.len() > MAX_RULE_PREDICATES {
                return Err(AuthorizationError::ExceedsBound("rule predicates"));
            }
            if rule
                .claim_predicates
                .iter()
                .any(|predicate| !indexed_fields.contains(&predicate.field))
            {
                let field = rule
                    .claim_predicates
                    .iter()
                    .find(|predicate| !indexed_fields.contains(&predicate.field))
                    .map_or_else(String::new, |predicate| predicate.field.clone());
                return Err(AuthorizationError::UnindexedPredicate(field));
            }
            if let Some(relationship) = &rule.relationship
                && (relationship.max_hops == 0
                    || relationship.max_hops > MAX_RELATIONSHIP_HOPS
                    || relationship.max_matches == 0
                    || relationship.max_matches > MAX_RELATIONSHIP_MATCHES)
            {
                return Err(AuthorizationError::UnboundedRelationship);
            }
        }
        rules.sort_by_key(PolicyRule::canonical_key);
        if rules.windows(2).any(|pair| pair[0].canonical_key() == pair[1].canonical_key()) {
            return Err(AuthorizationError::DuplicateRule);
        }
        Ok(Self { version: AUTHORIZATION_FORMAT_VERSION, generation, indexed_fields, rules })
    }

    /// Returns the policy language version.
    #[must_use]
    pub const fn version(&self) -> u16 {
        self.version
    }

    /// Returns the policy generation.
    #[must_use]
    pub const fn generation(&self) -> u64 {
        self.generation
    }

    /// Returns grants in canonical order.
    #[must_use]
    pub fn rules(&self) -> &[PolicyRule] {
        &self.rules
    }

    /// Returns a stable textual canonical representation for dependency keys.
    #[must_use]
    pub fn canonical_bytes(&self) -> Vec<u8> {
        let fields = self.indexed_fields.iter().cloned().collect::<Vec<_>>().join(",");
        format!(
            "v={};g={};i={};r={}",
            self.version,
            self.generation,
            fields,
            self.rules.iter().map(PolicyRule::canonical_key).collect::<Vec<_>>().join("\n")
        )
        .into_bytes()
    }
}

/// A request to evaluate one bounded document or query operation.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct AuthorizationRequest {
    scope: ResourceScope,
    audience: String,
    action: Action,
    collection: String,
    fields: BTreeMap<String, AttributeValue>,
    relationship_evidence: BTreeSet<String>,
    now: u64,
}

impl AuthorizationRequest {
    /// Creates an evaluated request with bounded field and relationship maps.
    ///
    /// # Errors
    ///
    /// Returns [`AuthorizationError::InvalidIdentifier`] for malformed scope
    /// selectors, or a typed bound error when fields or evidence exceed limits.
    pub fn try_new(
        scope: ResourceScope,
        audience: &str,
        action: Action,
        collection: &str,
        fields: BTreeMap<String, AttributeValue>,
        relationship_evidence: BTreeSet<String>,
        now: u64,
    ) -> Result<Self, AuthorizationError> {
        if !valid_scope_identifier(audience) || !valid_scope_identifier(collection) {
            return Err(AuthorizationError::InvalidIdentifier);
        }
        if fields.len() > MAX_INDEXED_FIELDS || fields.keys().any(|field| !valid_field_path(field))
        {
            return Err(AuthorizationError::ExceedsBound("document fields"));
        }
        if relationship_evidence.len() > MAX_RELATIONSHIP_MATCHES as usize {
            return Err(AuthorizationError::ExceedsBound("relationship evidence"));
        }
        if relationship_evidence.iter().any(|evidence| {
            evidence.len() > MAX_IDENTIFIER_BYTES || evidence.chars().any(char::is_control)
        }) {
            return Err(AuthorizationError::InvalidIdentifier);
        }
        Ok(Self {
            scope,
            audience: audience.to_owned(),
            action,
            collection: collection.to_owned(),
            fields,
            relationship_evidence,
            now,
        })
    }
}

/// Why a request was refused.  These variants are intentionally typed so a
/// gateway can audit a denial without receiving sensitive claim contents.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum DenialReason {
    /// The tenant, Project, or database did not match.
    ScopeMismatch,
    /// The request audience did not match the verified context.
    AudienceMismatch,
    /// The context had expired.
    Expired,
    /// The context and policy generations differed.
    PolicyGenerationMismatch,
    /// The principal did not carry the requested action.
    ActionNotGranted,
    /// No policy rule matched the operation and collection.
    NoMatchingRule,
    /// A claim or bounded relationship predicate did not match.
    PredicateFailed,
}

/// The typed result of authorization evaluation.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum AuthorizationDecision {
    /// The operation is allowed and the map has already been field-redacted.
    Allow {
        /// The authorized, field-projected values safe to materialize.
        fields: BTreeMap<String, AttributeValue>,
    },
    /// The operation is refused without exposing policy or claim material.
    Deny(DenialReason),
}

impl AuthorizationDecision {
    /// Returns whether the operation was allowed.
    #[must_use]
    pub const fn is_allowed(&self) -> bool {
        matches!(self, Self::Allow { .. })
    }

    /// Returns redacted fields for an allowed decision, or an empty map for a denial.
    #[must_use]
    pub fn fields(&self) -> BTreeMap<String, AttributeValue> {
        match self {
            Self::Allow { fields } => fields.clone(),
            Self::Deny(_) => BTreeMap::new(),
        }
    }
}

/// Evaluates an opaque context against a validated deterministic policy.
#[must_use]
pub fn evaluate(
    context: &PrincipalContext,
    policy: &Policy,
    request: &AuthorizationRequest,
) -> AuthorizationDecision {
    if context.scope != request.scope {
        return AuthorizationDecision::Deny(DenialReason::ScopeMismatch);
    }
    if context.audience != request.audience {
        return AuthorizationDecision::Deny(DenialReason::AudienceMismatch);
    }
    if request.now < context.issued_at || request.now >= context.expires_at {
        return AuthorizationDecision::Deny(DenialReason::Expired);
    }
    if context.policy_generation != policy.generation {
        return AuthorizationDecision::Deny(DenialReason::PolicyGenerationMismatch);
    }
    if !context.actions.contains(&request.action) {
        return AuthorizationDecision::Deny(DenialReason::ActionNotGranted);
    }
    let operation = Operation::from(request.action);
    let mut matched_rule = false;
    for rule in policy.rules.iter().filter(|rule| {
        rule.operation == operation
            && (rule.collection == request.collection || rule.collection == "*")
    }) {
        matched_rule = true;
        let claims_match = rule.claim_predicates.iter().all(|predicate| {
            let Some(document_value) = request.fields.get(&predicate.field) else {
                return false;
            };
            let Some(claim_value) = context.claims.0.get(&predicate.claim) else {
                return false;
            };
            compare(document_value, claim_value, predicate.comparison)
        });
        let relationship_matches = rule
            .relationship
            .as_ref()
            .is_none_or(|relationship| request.relationship_evidence.contains(&relationship.key()));
        // Grants are alternatives: a failed conditional grant must not mask
        // a later grant for the same operation and collection. Rules are in
        // canonical order, so the first successful projection is stable.
        if claims_match && relationship_matches {
            return AuthorizationDecision::Allow { fields: rule.projection.apply(&request.fields) };
        }
    }
    if matched_rule {
        AuthorizationDecision::Deny(DenialReason::PredicateFailed)
    } else {
        AuthorizationDecision::Deny(DenialReason::NoMatchingRule)
    }
}

fn compare(left: &AttributeValue, right: &AttributeValue, comparison: ClaimComparison) -> bool {
    match comparison {
        ClaimComparison::Equals => left == right,
        ClaimComparison::NotEquals => left != right,
        ClaimComparison::LessThan
        | ClaimComparison::LessOrEqual
        | ClaimComparison::GreaterThan
        | ClaimComparison::GreaterOrEqual => {
            let ordering = match (left, right) {
                (AttributeValue::Boolean(left), AttributeValue::Boolean(right)) => left.cmp(right),
                (AttributeValue::Integer(left), AttributeValue::Integer(right)) => left.cmp(right),
                (AttributeValue::String(left), AttributeValue::String(right)) => left.cmp(right),
                // An ordering between unlike claim types is not meaningful and
                // must not become an accidental authorization grant.
                _ => return false,
            };
            match comparison {
                ClaimComparison::LessThan => ordering.is_lt(),
                ClaimComparison::LessOrEqual => !ordering.is_gt(),
                ClaimComparison::GreaterThan => ordering.is_gt(),
                ClaimComparison::GreaterOrEqual => !ordering.is_lt(),
                ClaimComparison::Equals | ClaimComparison::NotEquals => unreachable!(),
            }
        }
    }
}

#[cfg(test)]
mod generation_model_tests;
#[cfg(test)]
mod tests;
