//! Engine-owned authorization gateway.
//!
//! This is deliberately narrower than the public Engine API. It verifies a
//! gateway envelope before obtaining an engine snapshot, and it consults the
//! durable policy store for every operation.

use std::{
    collections::BTreeMap,
    fmt,
    sync::{Arc, Mutex},
};

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
    policy_store: Arc<Mutex<AuthorizationPolicyStore>>,
    commit_fence: Arc<Mutex<()>>,
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
        let commit_fence = policy_store.commit_fence();
        Ok(Self {
            engine,
            expected_scope,
            environment,
            gateway_spiffe_principal,
            verifier,
            replay_guard: Mutex::new(PrincipalContextReplayGuard::new()),
            policy_store: Arc::new(Mutex::new(policy_store)),
            commit_fence,
        })
    }

    /// Binds a gateway to an already shared policy store. All gateways made
    /// from the handle share both policy state and commit serialization.
    pub fn new_shared(
        engine: Engine,
        expected_scope: ResourceScope,
        environment: impl Into<String>,
        gateway_spiffe_principal: impl Into<String>,
        verifier: PrincipalContextVerifier,
        policy_store: Arc<Mutex<AuthorizationPolicyStore>>,
    ) -> Result<Self, AuthorizedEngineError> {
        let environment = environment.into();
        let gateway_spiffe_principal = gateway_spiffe_principal.into();
        if environment.is_empty() || gateway_spiffe_principal.is_empty() {
            return Err(AuthorizedEngineError::BindingMismatch);
        }
        let commit_fence = policy_store
            .lock()
            .map_err(|_| {
                AuthorizedEngineError::Authorization(AuthorizationError::PolicyStoreUnavailable)
            })?
            .commit_fence();
        Ok(Self {
            engine,
            expected_scope,
            environment,
            gateway_spiffe_principal,
            verifier,
            replay_guard: Mutex::new(PrincipalContextReplayGuard::new()),
            policy_store,
            commit_fence,
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
        self.policy_decision(&session.context, request)
    }

    /// Publishes a policy through the gateway's synchronized durable store.
    /// Existing bound sessions observe its generation on their next request.
    pub fn publish_policy(
        &self,
        scope: ResourceScope,
        policy: crate::authorization::Policy,
    ) -> Result<(), AuthorizedEngineError> {
        if scope != self.expected_scope {
            return Err(AuthorizedEngineError::BindingMismatch);
        }
        self.policy_store
            .lock()
            .map_err(|_| {
                AuthorizedEngineError::Authorization(AuthorizationError::PolicyStoreUnavailable)
            })?
            .publish(scope, policy)
            .map_err(AuthorizedEngineError::Authorization)
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
    ///
    /// This is a one-shot convenience operation. Call [`Self::bind`] and
    /// [`Self::read_fields_bound`] when a client channel performs multiple
    /// operations with one envelope binding.
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
        self.policy_decision(context, &request)
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
        let operation_text = format!("authorized-write-{}-{}", session.context.session_id(), now);
        let operation = crate::types::OperationId::try_new(&operation_text)
            .map_err(|_| AuthorizedEngineError::BindingMismatch)?;
        self.write_with_operation(session, document, expected, operation, now)
    }

    /// Writes with a caller-supplied stable operation identity, preserving
    /// Engine's idempotent retry semantics.
    pub fn write_with_operation(
        &self,
        session: &AuthorizedSession,
        document: crate::types::Document,
        expected: crate::types::ExpectedVersion,
        operation: crate::types::OperationId,
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
        let store = self.policy_store.lock().map_err(|_| {
            AuthorizedEngineError::Authorization(AuthorizationError::PolicyStoreUnavailable)
        })?;
        let fence = self.commit_fence.lock().map_err(|_| {
            AuthorizedEngineError::Authorization(AuthorizationError::PolicyStoreUnavailable)
        })?;
        if !store.evaluate(context, &request).is_allowed() {
            return Err(AuthorizedEngineError::Denied);
        }
        let tenant = self.tenant_for(context)?;
        let snapshot = self.engine.read_snapshot(&tenant).map_err(AuthorizedEngineError::Engine)?;
        let mut transaction = self
            .engine
            .begin(&tenant, snapshot, operation)
            .map_err(AuthorizedEngineError::Engine)?;
        transaction.put(document, expected).map_err(AuthorizedEngineError::Engine)?;
        if !store.evaluate(context, &request).is_allowed() {
            return Err(AuthorizedEngineError::Denied);
        }
        let outcome = transaction.commit(crate::types::LocalDurability::Synced);
        drop(fence);
        Ok(outcome)
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
        let operation_text = format!("authorized-delete-{}-{}", session.context.session_id(), now);
        let operation = crate::types::OperationId::try_new(&operation_text)
            .map_err(|_| AuthorizedEngineError::BindingMismatch)?;
        self.delete_with_operation(session, collection, document_id, expected, operation, now)
    }

    /// Deletes with a caller-supplied stable operation identity.
    pub fn delete_with_operation(
        &self,
        session: &AuthorizedSession,
        collection: &CollectionName,
        document_id: DocumentId,
        expected: crate::types::ExpectedVersion,
        operation: crate::types::OperationId,
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
        let tenant = self.tenant_for(context)?;
        let store = self.policy_store.lock().map_err(|_| {
            AuthorizedEngineError::Authorization(AuthorizationError::PolicyStoreUnavailable)
        })?;
        let fence = self.commit_fence.lock().map_err(|_| {
            AuthorizedEngineError::Authorization(AuthorizationError::PolicyStoreUnavailable)
        })?;
        if !store.evaluate(context, &request).is_allowed() {
            return Err(AuthorizedEngineError::Denied);
        }
        let snapshot = self.engine.read_snapshot(&tenant).map_err(AuthorizedEngineError::Engine)?;
        let mut transaction = self
            .engine
            .begin(&tenant, snapshot, operation)
            .map_err(AuthorizedEngineError::Engine)?;
        transaction.delete_in_collection(collection, document_id, expected);
        if !store.evaluate(context, &request).is_allowed() {
            return Err(AuthorizedEngineError::Denied);
        }
        let outcome = transaction.commit(crate::types::LocalDurability::Synced);
        drop(fence);
        Ok(outcome)
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

    fn policy_decision(
        &self,
        context: &PrincipalContext,
        request: &AuthorizationRequest,
    ) -> Result<AuthorizationDecision, AuthorizedEngineError> {
        let store = self.policy_store.lock().map_err(|_| {
            AuthorizedEngineError::Authorization(AuthorizationError::PolicyStoreUnavailable)
        })?;
        Ok(store.evaluate(context, request))
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

#[cfg(test)]
mod tests {
    use super::*;
    use crate::authorization::{
        Action, AuthorizationCryptoMode, AuthorizationRequest, Operation, Policy, PolicyRule,
        PrincipalClaims, PrincipalContextKey, PrincipalContextKeySet, PrincipalContextPayload,
        PrincipalContextSigner,
    };
    use crate::identity::{
        AuthorizationPolicyUlid, PrincipalGrantUlid, PrincipalSessionUlid, PrincipalUlid,
        ProjectUlid, TenantUlid,
    };
    use std::{
        collections::BTreeSet,
        fs,
        sync::atomic::{AtomicU64, Ordering},
    };

    static COUNTER: AtomicU64 = AtomicU64::new(0);

    fn path(name: &str) -> std::path::PathBuf {
        std::env::temp_dir().join(format!(
            "terrabase-authorized-{name}-{}-{}",
            std::process::id(),
            COUNTER.fetch_add(1, Ordering::Relaxed)
        ))
    }

    struct Fixture {
        gateway: AuthorizedEngine,
        envelope: PrincipalContextEnvelope,
        envelope_two: PrincipalContextEnvelope,
        scope: ResourceScope,
        grant: PrincipalGrantUlid,
        policy_id: AuthorizationPolicyUlid,
        session: PrincipalSessionUlid,
        session_two: PrincipalSessionUlid,
        engine_path: std::path::PathBuf,
    }

    #[allow(
        clippy::too_many_lines,
        reason = "the fixture binds the complete signed gateway context"
    )]
    fn fixture() -> Fixture {
        let engine_path = path("engine");
        let policy_path = engine_path.join("authorization.journal");
        let engine =
            Engine::open(crate::engine::EngineConfig::durable(&engine_path).expect("config"))
                .expect("engine");
        let scope = ResourceScope::try_new(
            TenantUlid::from_wire_bytes([1; 16]),
            ProjectUlid::from_wire_bytes([2; 16]),
            engine.database_id(),
        )
        .expect("scope");
        let grant = PrincipalGrantUlid::from_wire_bytes([4; 16]);
        let grant_two = PrincipalGrantUlid::from_wire_bytes([8; 16]);
        let policy_id = AuthorizationPolicyUlid::from_wire_bytes([5; 16]);
        let rules = [
            Operation::Read,
            Operation::Query,
            Operation::Mutate,
            Operation::Subscribe,
            Operation::Schema,
            Operation::Admin,
        ]
        .into_iter()
        .enumerate()
        .map(|(index, operation)| {
            PolicyRule::try_new_with_id(
                if index == 0 { grant_two } else { grant },
                operation,
                "documents",
            )
            .expect("rule")
        })
        .collect::<Vec<_>>();
        let policy = Policy::try_new_with_id(policy_id, 1, std::iter::empty::<String>(), rules)
            .expect("policy");
        let mut store = AuthorizationPolicyStore::open(&policy_path, [42; 32]).expect("store");
        store.publish(scope.clone(), policy).expect("publish");
        let signer = PrincipalContextSigner::from_pkcs8(
            "authorized-test",
            aws_lc_rs::signature::EcdsaKeyPair::generate_pkcs8(
                &aws_lc_rs::signature::ECDSA_P256_SHA256_FIXED_SIGNING,
                &aws_lc_rs::rand::SystemRandom::new(),
            )
            .expect("key")
            .as_ref()
            .to_vec(),
        )
        .expect("signer");
        let make_payload = |principal, session, grant| {
            PrincipalContextPayload::try_new(
                "production",
                "spiffe://terrascale/gateway",
                "terrascale-go",
                "terrascale.database",
                scope.clone(),
                principal,
                session,
                grant,
                policy_id,
                BTreeSet::from([
                    Action::Read,
                    Action::Query,
                    Action::Mutate,
                    Action::Subscribe,
                    Action::Schema,
                    Action::Admin,
                ]),
                PrincipalClaims::default(),
                1,
                1,
                100,
            )
            .expect("payload")
        };
        let session = PrincipalSessionUlid::from_wire_bytes([7; 16]);
        let session_two = PrincipalSessionUlid::from_wire_bytes([10; 16]);
        let envelope = signer
            .sign(
                &make_payload(PrincipalUlid::from_wire_bytes([6; 16]), session, grant),
                AuthorizationCryptoMode::LocalDevelopment,
            )
            .expect("sign");
        let envelope_two = signer
            .sign(
                &make_payload(PrincipalUlid::from_wire_bytes([9; 16]), session_two, grant_two),
                AuthorizationCryptoMode::LocalDevelopment,
            )
            .expect("sign");
        let key = PrincipalContextKey::new(signer.key_id(), signer.public_key().expect("public"))
            .expect("key");
        let verifier = crate::authorization::PrincipalContextVerifier::new(
            AuthorizationCryptoMode::LocalDevelopment,
            "production",
            "spiffe://terrascale/gateway",
            "terrascale-go",
            "terrascale.database",
            PrincipalContextKeySet::try_new([key]).expect("keys"),
        )
        .expect("verifier");
        let gateway = AuthorizedEngine::new(
            engine,
            scope.clone(),
            "production",
            "spiffe://terrascale/gateway",
            verifier,
            store,
        )
        .expect("gateway");
        Fixture {
            gateway,
            envelope,
            envelope_two,
            scope,
            grant,
            policy_id,
            session,
            session_two,
            engine_path,
        }
    }

    #[test]
    fn one_bind_supports_repeated_bound_checks_without_replaying_envelope() {
        let Fixture { gateway, envelope, scope, grant, policy_id, session, engine_path, .. } =
            fixture();
        assert!(matches!(
            gateway.bind(&envelope, PrincipalSessionUlid::from_wire_bytes([8; 16]), 10),
            Err(AuthorizedEngineError::Authorization(AuthorizationError::ContextBindingMismatch))
        ));
        let session = gateway.bind(&envelope, session, 10).expect("bind");
        assert!(matches!(
            gateway.bind(&envelope, PrincipalSessionUlid::from_wire_bytes([7; 16]), 10),
            Err(AuthorizedEngineError::Authorization(AuthorizationError::ContextReplay))
        ));
        let request = AuthorizationRequest::try_new(
            scope.clone(),
            "terrascale.database",
            Action::Read,
            "documents",
            BTreeMap::default(),
            BTreeSet::default(),
            10,
        )
        .expect("request");
        assert!(gateway.authorize_bound(&session, &request, 10).expect("authorize").is_allowed());
        assert!(gateway.authorize_bound(&session, &request, 11).expect("authorize").is_allowed());
        assert!(matches!(
            gateway.authorize_bound(&session, &request, 100),
            Err(AuthorizedEngineError::Authorization(AuthorizationError::ContextExpired))
        ));
        let replacement = Policy::try_new_with_id(
            policy_id,
            2,
            std::iter::empty::<String>(),
            [PolicyRule::try_new_with_id(grant, Operation::Read, "documents").expect("rule")],
        )
        .expect("replacement");
        gateway.publish_policy(scope, replacement).expect("replace policy");
        assert!(
            !gateway.authorize_bound(&session, &request, 11).expect("stale decision").is_allowed()
        );
        assert!(!format!("{gateway:?}").contains("authorized-test"));
        drop(gateway);
        let _ = fs::remove_dir_all(engine_path);
    }

    #[test]
    fn category_gateways_accept_only_their_exact_action() {
        let Fixture { gateway, envelope, scope, session, engine_path, .. } = fixture();
        let session = gateway.bind(&envelope, session, 10).expect("bind");
        let actions = [
            Action::Read,
            Action::Query,
            Action::Mutate,
            Action::Subscribe,
            Action::Schema,
            Action::Admin,
        ];
        let authorize = |action, request: &AuthorizationRequest| match action {
            Action::Read => gateway.authorize_family_read(&session, request, 10),
            Action::Query => gateway.authorize_query(&session, request, 10),
            Action::Mutate => gateway.authorize_family_write(&session, request, 10),
            Action::Subscribe => gateway.authorize_subscription(&session, request, 10),
            Action::Schema => gateway.authorize_schema(&session, request, 10),
            Action::Admin => gateway.authorize_admin(&session, request, 10),
        };

        for action in actions {
            let request = AuthorizationRequest::try_new(
                scope.clone(),
                "terrascale.database",
                action,
                "documents",
                BTreeMap::new(),
                BTreeSet::new(),
                10,
            )
            .expect("request");
            assert!(authorize(action, &request).expect("exact action").is_allowed());

            for other_action in actions {
                if other_action == action {
                    continue;
                }
                let request = AuthorizationRequest::try_new(
                    scope.clone(),
                    "terrascale.database",
                    other_action,
                    "documents",
                    BTreeMap::new(),
                    BTreeSet::new(),
                    10,
                )
                .expect("request");
                assert!(matches!(authorize(action, &request), Err(AuthorizedEngineError::Denied)));
            }
        }
        drop(session);
        let _ = fs::remove_dir_all(engine_path);
    }

    #[test]
    fn concurrent_sessions_reuse_bound_contexts_without_cross_session_replay() {
        let Fixture {
            gateway,
            envelope,
            envelope_two,
            scope,
            session,
            session_two,
            engine_path,
            ..
        } = fixture();
        let first_session = gateway.bind(&envelope, session, 10).expect("first bind");
        let second_session = gateway.bind(&envelope_two, session_two, 10).expect("second bind");
        let gateway = std::sync::Arc::new(gateway);
        let request = AuthorizationRequest::try_new(
            scope,
            "terrascale.database",
            Action::Query,
            "documents",
            BTreeMap::new(),
            BTreeSet::new(),
            10,
        )
        .expect("request");
        let first_gateway = std::sync::Arc::clone(&gateway);
        let second_gateway = std::sync::Arc::clone(&gateway);
        let second_request = request.clone();
        let first = std::thread::spawn(move || {
            for now in 10..30 {
                assert!(
                    first_gateway
                        .authorize_bound(&first_session, &request, now)
                        .expect("first authorization")
                        .is_allowed()
                );
            }
        });
        let second = std::thread::spawn(move || {
            for now in 10..30 {
                assert!(
                    second_gateway
                        .authorize_bound(&second_session, &second_request, now)
                        .expect("second authorization")
                        .is_allowed()
                );
            }
        });
        first.join().expect("first thread");
        second.join().expect("second thread");
        drop(gateway);
        let _ = fs::remove_dir_all(engine_path);
    }

    #[test]
    fn unsupported_document_values_are_not_coerced_into_authorization_claims() {
        let document = crate::types::Document::new(
            crate::types::DocumentId::try_new("doc").expect("id"),
            crate::types::CollectionName::try_new("documents").expect("collection"),
            DocumentValue::Object(
                [
                    ("null".to_owned(), DocumentValue::Null),
                    ("float".to_owned(), DocumentValue::Number(1.5)),
                    ("array".to_owned(), DocumentValue::Array(vec![DocumentValue::Integer(1)])),
                    (
                        "nested".to_owned(),
                        DocumentValue::Object(
                            [("owner".to_owned(), DocumentValue::String("acme".to_owned()))]
                                .into_iter()
                                .collect(),
                        ),
                    ),
                ]
                .into_iter()
                .collect(),
            ),
        );
        assert!(scalar_fields(&document).is_empty());
    }
}
