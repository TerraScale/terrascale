//! Engine-owned authorization gateway.
//!
//! This is deliberately narrower than the public Engine API. It verifies a
//! gateway envelope before obtaining an engine snapshot, and it consults the
//! durable policy store for every operation.

use std::{collections::BTreeMap, fmt, sync::Mutex};

use crate::{
    authorization::{
        AuthorizationDecision, AuthorizationError, AuthorizationPolicyStore, AuthorizationRequest,
        PrincipalContext, PrincipalContextEnvelope, PrincipalContextReplayGuard,
        PrincipalContextVerifier, ResourceScope,
    },
    identity::PrincipalSessionUlid,
    types::{CollectionName, DocumentId, DocumentValue, TenantId},
};

use super::{Engine, EngineError};

/// Fail-closed failures from the authorized engine gateway.
#[derive(Debug)]
pub enum AuthorizedEngineError {
    /// Envelope verification, binding, expiry, or replay failed.
    Authorization(AuthorizationError),
    /// The underlying engine could not read the requested document.
    Engine(EngineError),
    /// The request did not satisfy the fixed gateway binding.
    BindingMismatch,
    /// The durable policy denied the requested operation.
    Denied,
}

impl fmt::Display for AuthorizedEngineError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Authorization(error) => error.fmt(formatter),
            Self::Engine(_) => formatter.write_str("authorized engine operation failed"),
            Self::BindingMismatch => formatter.write_str("authorized engine binding mismatch"),
            Self::Denied => formatter.write_str("authorized engine operation denied"),
        }
    }
}

impl std::error::Error for AuthorizedEngineError {}

/// An authenticated channel binding. The context is private and can only be
/// obtained from [`AuthorizedEngine::bind`].
pub struct AuthorizedSession {
    context: PrincipalContext,
}

impl fmt::Debug for AuthorizedSession {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.debug_struct("AuthorizedSession").finish_non_exhaustive()
    }
}

/// The only engine operation surface that accepts public signed contexts.
pub struct AuthorizedEngine {
    engine: Engine,
    expected_scope: ResourceScope,
    environment: String,
    gateway_spiffe_principal: String,
    verifier: PrincipalContextVerifier,
    replay_guard: Mutex<PrincipalContextReplayGuard>,
    policy_store: AuthorizationPolicyStore,
}

impl fmt::Debug for AuthorizedEngine {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter
            .debug_struct("AuthorizedEngine")
            .field("expected_scope", &self.expected_scope)
            .field("environment", &self.environment)
            .field("gateway_spiffe_principal", &self.gateway_spiffe_principal)
            .finish_non_exhaustive()
    }
}

#[allow(clippy::missing_errors_doc)]
impl AuthorizedEngine {
    /// Binds an Engine to one verifier, scope, environment, gateway, and
    /// durable policy store.
    pub fn new(
        engine: Engine,
        expected_scope: ResourceScope,
        environment: impl Into<String>,
        gateway_spiffe_principal: impl Into<String>,
        verifier: PrincipalContextVerifier,
        policy_store: AuthorizationPolicyStore,
    ) -> Result<Self, AuthorizedEngineError> {
        let environment = environment.into();
        let gateway_spiffe_principal = gateway_spiffe_principal.into();
        if environment.is_empty() || gateway_spiffe_principal.is_empty() {
            return Err(AuthorizedEngineError::BindingMismatch);
        }
        Ok(Self {
            engine,
            expected_scope,
            environment,
            gateway_spiffe_principal,
            verifier,
            replay_guard: Mutex::new(PrincipalContextReplayGuard::new()),
            policy_store,
        })
    }

    /// Authenticates one logical client channel. The returned handle may be
    /// reused for multiple operations, while every operation still checks its
    /// current expiry and durable policy generation.
    pub fn bind(
        &self,
        envelope: &PrincipalContextEnvelope,
        channel_binding: PrincipalSessionUlid,
        now: u64,
    ) -> Result<AuthorizedSession, AuthorizedEngineError> {
        let mut guard = self.replay_guard.lock().map_err(|_| {
            AuthorizedEngineError::Authorization(AuthorizationError::PolicyStoreUnavailable)
        })?;
        let context = self
            .verifier
            .verify_with_replay(envelope, now, channel_binding, &mut guard)
            .map_err(AuthorizedEngineError::Authorization)?;
        self.check_binding(&context)?;
        Ok(AuthorizedSession { context })
    }

    /// Verifies and evaluates a request against the current durable policy.
    pub fn authorize(
        &self,
        envelope: &PrincipalContextEnvelope,
        channel_binding: PrincipalSessionUlid,
        request: &AuthorizationRequest,
        now: u64,
    ) -> Result<AuthorizationDecision, AuthorizedEngineError> {
        let session = self.bind(envelope, channel_binding, now)?;
        self.authorize_bound(&session, request, now)
    }

    /// Evaluates a request using an already authenticated channel binding.
    pub fn authorize_bound(
        &self,
        session: &AuthorizedSession,
        request: &AuthorizationRequest,
        now: u64,
    ) -> Result<AuthorizationDecision, AuthorizedEngineError> {
        self.check_session(session, now)?;
        Ok(self.policy_store.evaluate(&session.context, request))
    }

    /// Authorizes a bounded query request. Query planning and execution stay
    /// in the query module; this seam enforces the exact Action mapping first.
    pub fn authorize_query(
        &self,
        session: &AuthorizedSession,
        request: &AuthorizationRequest,
        now: u64,
    ) -> Result<AuthorizationDecision, AuthorizedEngineError> {
        self.authorize_category(session, request, crate::authorization::Action::Query, now)
    }

    /// Authorizes a family read request.
    pub fn authorize_family_read(
        &self,
        session: &AuthorizedSession,
        request: &AuthorizationRequest,
        now: u64,
    ) -> Result<AuthorizationDecision, AuthorizedEngineError> {
        self.authorize_category(session, request, crate::authorization::Action::Read, now)
    }

    /// Authorizes a family write request.
    pub fn authorize_family_write(
        &self,
        session: &AuthorizedSession,
        request: &AuthorizationRequest,
        now: u64,
    ) -> Result<AuthorizationDecision, AuthorizedEngineError> {
        self.authorize_category(session, request, crate::authorization::Action::Mutate, now)
    }

    /// Authorizes a schema lifecycle request.
    pub fn authorize_schema(
        &self,
        session: &AuthorizedSession,
        request: &AuthorizationRequest,
        now: u64,
    ) -> Result<AuthorizationDecision, AuthorizedEngineError> {
        self.authorize_category(session, request, crate::authorization::Action::Schema, now)
    }

    /// Authorizes a subscription request.
    pub fn authorize_subscription(
        &self,
        session: &AuthorizedSession,
        request: &AuthorizationRequest,
        now: u64,
    ) -> Result<AuthorizationDecision, AuthorizedEngineError> {
        self.authorize_category(session, request, crate::authorization::Action::Subscribe, now)
    }

    /// Authorizes an administrative request.
    pub fn authorize_admin(
        &self,
        session: &AuthorizedSession,
        request: &AuthorizationRequest,
        now: u64,
    ) -> Result<AuthorizationDecision, AuthorizedEngineError> {
        self.authorize_category(session, request, crate::authorization::Action::Admin, now)
    }

    /// Alias for callers that treat the supplied request as a query or other
    /// read-only operation.
    pub fn authorize_request(
        &self,
        envelope: &PrincipalContextEnvelope,
        channel_binding: PrincipalSessionUlid,
        request: &AuthorizationRequest,
        now: u64,
    ) -> Result<AuthorizationDecision, AuthorizedEngineError> {
        self.authorize(envelope, channel_binding, request, now)
    }

    /// Reads one document and returns only scalar fields admitted by the
    /// policy projection. Policies with predicates are evaluated against the
    /// document before any field is returned.
    pub fn read_fields(
        &self,
        envelope: &PrincipalContextEnvelope,
        channel_binding: PrincipalSessionUlid,
        collection: &CollectionName,
        document_id: &DocumentId,
        now: u64,
    ) -> Result<AuthorizationDecision, AuthorizedEngineError> {
        let session = self.bind(envelope, channel_binding, now)?;
        self.read_fields_bound(&session, collection, document_id, now)
    }

    /// Reads through an already authenticated channel binding.
    pub fn read_fields_bound(
        &self,
        session: &AuthorizedSession,
        collection: &CollectionName,
        document_id: &DocumentId,
        now: u64,
    ) -> Result<AuthorizationDecision, AuthorizedEngineError> {
        self.check_session(session, now)?;
        let context = &session.context;
        let tenant = TenantId::try_new(&context.tenant_id().to_text())
            .map_err(|_| AuthorizedEngineError::BindingMismatch)?;
        let scope = self.engine.scope_for(&tenant);
        let snapshot = self.engine.read_snapshot(&scope).map_err(AuthorizedEngineError::Engine)?;
        let operation_text = format!("authorized-read-{}-{}", context.session_id(), now);
        let operation = crate::types::OperationId::try_new(&operation_text)
            .map_err(|_| AuthorizedEngineError::BindingMismatch)?;
        let transaction = self
            .engine
            .begin(&scope, snapshot, operation)
            .map_err(AuthorizedEngineError::Engine)?;
        let document = transaction
            .read_in_collection(collection, document_id)
            .map_err(AuthorizedEngineError::Engine)?;
        let fields = document.as_ref().map_or_else(BTreeMap::new, scalar_fields);
        let request = AuthorizationRequest::try_new(
            self.expected_scope.clone(),
            context.audience(),
            crate::authorization::Action::Read,
            collection.as_str(),
            fields,
            std::collections::BTreeSet::new(),
            now,
        )
        .map_err(AuthorizedEngineError::Authorization)?;
        Ok(self.policy_store.evaluate(context, &request))
    }

    /// Stages and commits one authorized document write. Authorization is
    /// checked before staging and again immediately before commit.
    pub fn write(
        &self,
        session: &AuthorizedSession,
        document: crate::types::Document,
        expected: crate::types::ExpectedVersion,
        now: u64,
    ) -> Result<super::CommitOutcome, AuthorizedEngineError> {
        self.check_session(session, now)?;
        let context = &session.context;
        let collection = document.collection().clone();
        let request = AuthorizationRequest::try_new(
            self.expected_scope.clone(),
            context.audience(),
            crate::authorization::Action::Mutate,
            collection.as_str(),
            scalar_fields(&document),
            std::collections::BTreeSet::new(),
            now,
        )
        .map_err(AuthorizedEngineError::Authorization)?;
        if !self.policy_store.evaluate(context, &request).is_allowed() {
            return Err(AuthorizedEngineError::Denied);
        }
        let tenant = self.tenant_for(context)?;
        let snapshot = self.engine.read_snapshot(&tenant).map_err(AuthorizedEngineError::Engine)?;
        let operation_text = format!("authorized-write-{}-{}", context.session_id(), now);
        let operation = crate::types::OperationId::try_new(&operation_text)
            .map_err(|_| AuthorizedEngineError::BindingMismatch)?;
        let mut transaction = self
            .engine
            .begin(&tenant, snapshot, operation)
            .map_err(AuthorizedEngineError::Engine)?;
        transaction.put(document, expected).map_err(AuthorizedEngineError::Engine)?;
        if !self.policy_store.evaluate(context, &request).is_allowed() {
            return Err(AuthorizedEngineError::Denied);
        }
        Ok(transaction.commit(crate::types::LocalDurability::Synced))
    }

    /// Stages and commits one authorized collection-scoped deletion.
    pub fn delete(
        &self,
        session: &AuthorizedSession,
        collection: &CollectionName,
        document_id: DocumentId,
        expected: crate::types::ExpectedVersion,
        now: u64,
    ) -> Result<super::CommitOutcome, AuthorizedEngineError> {
        self.check_session(session, now)?;
        let context = &session.context;
        let request = AuthorizationRequest::try_new(
            self.expected_scope.clone(),
            context.audience(),
            crate::authorization::Action::Mutate,
            collection.as_str(),
            BTreeMap::new(),
            std::collections::BTreeSet::new(),
            now,
        )
        .map_err(AuthorizedEngineError::Authorization)?;
        if !self.policy_store.evaluate(context, &request).is_allowed() {
            return Err(AuthorizedEngineError::Denied);
        }
        let tenant = self.tenant_for(context)?;
        let snapshot = self.engine.read_snapshot(&tenant).map_err(AuthorizedEngineError::Engine)?;
        let operation_text = format!("authorized-delete-{}-{}", context.session_id(), now);
        let operation = crate::types::OperationId::try_new(&operation_text)
            .map_err(|_| AuthorizedEngineError::BindingMismatch)?;
        let mut transaction = self
            .engine
            .begin(&tenant, snapshot, operation)
            .map_err(AuthorizedEngineError::Engine)?;
        transaction.delete_in_collection(collection, document_id, expected);
        if !self.policy_store.evaluate(context, &request).is_allowed() {
            return Err(AuthorizedEngineError::Denied);
        }
        Ok(transaction.commit(crate::types::LocalDurability::Synced))
    }

    /// Returns the owned Engine after the authorization wrapper is retired.
    pub fn into_engine(self) -> Engine {
        self.engine
    }

    fn check_binding(&self, context: &PrincipalContext) -> Result<(), AuthorizedEngineError> {
        let scope = ResourceScope::try_new(
            context.tenant_id(),
            context.project_id(),
            context.database_id(),
        )
        .map_err(|_| AuthorizedEngineError::BindingMismatch)?;
        if scope != self.expected_scope
            || context.database_id() != self.engine.database_id()
            || context.environment() != self.environment
            || context.gateway_spiffe_principal() != self.gateway_spiffe_principal
        {
            return Err(AuthorizedEngineError::BindingMismatch);
        }
        Ok(())
    }

    fn check_session(
        &self,
        session: &AuthorizedSession,
        now: u64,
    ) -> Result<(), AuthorizedEngineError> {
        self.check_binding(&session.context)?;
        if now < session.context.expires_at() {
            Ok(())
        } else {
            Err(AuthorizedEngineError::Authorization(AuthorizationError::ContextExpired))
        }
    }

    fn authorize_category(
        &self,
        session: &AuthorizedSession,
        request: &AuthorizationRequest,
        action: crate::authorization::Action,
        now: u64,
    ) -> Result<AuthorizationDecision, AuthorizedEngineError> {
        if request.action() != action {
            return Err(AuthorizedEngineError::Denied);
        }
        self.authorize_bound(session, request, now)
    }

    fn tenant_for(
        &self,
        context: &PrincipalContext,
    ) -> Result<crate::types::TenantScope, AuthorizedEngineError> {
        let tenant = TenantId::try_new(&context.tenant_id().to_text())
            .map_err(|_| AuthorizedEngineError::BindingMismatch)?;
        Ok(self.engine.scope_for(&tenant))
    }
}

fn scalar_fields(
    document: &crate::types::Document,
) -> BTreeMap<String, crate::authorization::AttributeValue> {
    let DocumentValue::Object(values) = document.value() else {
        return BTreeMap::new();
    };
    values
        .iter()
        .filter_map(|(field, value)| {
            let value = match value {
                DocumentValue::Boolean(value) => {
                    crate::authorization::AttributeValue::Boolean(*value)
                }
                DocumentValue::Integer(value) => {
                    crate::authorization::AttributeValue::Integer(*value)
                }
                DocumentValue::String(value) => {
                    crate::authorization::AttributeValue::String(value.clone())
                }
                DocumentValue::Null
                | DocumentValue::Number(_)
                | DocumentValue::Array(_)
                | DocumentValue::Object(_) => return None,
            };
            Some((field.clone(), value))
        })
        .collect()
}
