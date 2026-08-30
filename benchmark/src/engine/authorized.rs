//! Engine-owned authorization gateway.
//!
//! This is deliberately narrower than the public Engine API. It verifies a
//! gateway envelope before obtaining an engine snapshot, and it consults the
//! durable policy store for every operation.

use std::{collections::BTreeMap, fmt};

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
}

impl fmt::Display for AuthorizedEngineError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Authorization(error) => error.fmt(formatter),
            Self::Engine(_) => formatter.write_str("authorized engine operation failed"),
            Self::BindingMismatch => formatter.write_str("authorized engine binding mismatch"),
        }
    }
}

impl std::error::Error for AuthorizedEngineError {}

/// The only engine operation surface that accepts public signed contexts.
pub struct AuthorizedEngine {
    engine: Engine,
    expected_scope: ResourceScope,
    environment: String,
    gateway_spiffe_principal: String,
    verifier: PrincipalContextVerifier,
    replay_guard: PrincipalContextReplayGuard,
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
            replay_guard: PrincipalContextReplayGuard::new(),
            policy_store,
        })
    }

    /// Replaces the replay guard only for a new trusted channel/session.
    pub fn reset_channel(&mut self) {
        self.replay_guard = PrincipalContextReplayGuard::new();
    }

    /// Verifies and evaluates a request against the current durable policy.
    pub fn authorize(
        &mut self,
        envelope: &PrincipalContextEnvelope,
        channel_binding: PrincipalSessionUlid,
        request: &AuthorizationRequest,
        now: u64,
    ) -> Result<AuthorizationDecision, AuthorizedEngineError> {
        let context = self
            .verifier
            .verify_with_replay(envelope, now, channel_binding, &mut self.replay_guard)
            .map_err(AuthorizedEngineError::Authorization)?;
        self.check_binding(&context)?;
        Ok(self.policy_store.evaluate(&context, request))
    }

    /// Alias for callers that treat the supplied request as a query or other
    /// read-only operation.
    pub fn authorize_request(
        &mut self,
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
        &mut self,
        envelope: &PrincipalContextEnvelope,
        channel_binding: PrincipalSessionUlid,
        collection: &CollectionName,
        document_id: &DocumentId,
        now: u64,
    ) -> Result<AuthorizationDecision, AuthorizedEngineError> {
        let context = self
            .verifier
            .verify_with_replay(envelope, now, channel_binding, &mut self.replay_guard)
            .map_err(AuthorizedEngineError::Authorization)?;
        self.check_binding(&context)?;
        let preflight = AuthorizationRequest::try_new(
            self.expected_scope.clone(),
            context.audience(),
            crate::authorization::Action::Read,
            collection.as_str(),
            BTreeMap::new(),
            std::collections::BTreeSet::new(),
            now,
        )
        .map_err(AuthorizedEngineError::Authorization)?;
        let preflight_decision = self.policy_store.evaluate(&context, &preflight);
        if !preflight_decision.is_allowed() {
            return Ok(preflight_decision);
        }
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
        Ok(self.policy_store.evaluate(&context, &request))
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
