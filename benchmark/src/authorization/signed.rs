//! Signed gateway principal contexts.
//!
//! The wire format in this module is deliberately small and private to the
//! database boundary. The verifier authenticates the opaque payload before it
//! attempts to parse any identity, action, or claim in it.

use std::collections::{BTreeMap, BTreeSet};

use aws_lc_rs::rand::SystemRandom;
use aws_lc_rs::signature::{
    ECDSA_P256_SHA256_FIXED, ECDSA_P256_SHA256_FIXED_SIGNING, EcdsaKeyPair, KeyPair,
    UnparsedPublicKey,
};
use zeroize::{Zeroize, Zeroizing};

use crate::identity::{
    AuthorizationPolicyUlid, DatabaseUlid, PrincipalGrantUlid, PrincipalSessionUlid, PrincipalUlid,
    ProjectUlid, TenantUlid, ULID_BINARY_BYTES,
};

use super::{
    Action, AuthorizationError, MAX_PRINCIPAL_ACTIONS, MAX_PRINCIPAL_CLAIMS, PrincipalClaims,
    PrincipalContext, ResourceScope, VerifiedPrincipalInput,
};

const ENVELOPE_MAGIC: [u8; 4] = *b"TPCX";
const ENVELOPE_VERSION: u8 = 1;
const PAYLOAD_MAGIC: [u8; 4] = *b"TPC1";
const PAYLOAD_VERSION: u8 = 1;
const MAX_KEY_ID_BYTES: usize = 64;
const MAX_TEXT_BYTES: usize = 255;
const MAX_CONTEXT_PAYLOAD_BYTES: usize = 16 * 1024;
const MAX_CONTEXT_ENVELOPE_BYTES: usize = MAX_CONTEXT_PAYLOAD_BYTES + 512;
const SIGNATURE_BYTES: usize = 64;
const MAX_CONTEXT_LIFETIME_SECONDS: u64 = 5 * 60;
const MAX_REPLAY_ENTRIES: usize = 65_536;

/// Selects the crypto and key provenance policy for authorization.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum AuthorizationCryptoMode {
    /// Local development mode, which may use an owner-only development key.
    LocalDevelopment,
    /// Production mode, which requires an operational AWS-LC FIPS module and
    /// a production-provenance public key.
    ProductionFips,
}

impl AuthorizationCryptoMode {
    fn is_production(self) -> bool {
        matches!(self, Self::ProductionFips)
    }
}

/// Provenance recorded for a trusted signing or verification key.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum SigningKeyProvenance {
    /// A key held by an owner-controlled local development installation.
    LocalDevelopment,
    /// A key distributed by the production key-management authority.
    Production,
}

/// A bounded, typed principal-context payload.
#[derive(Clone, Eq, PartialEq)]
pub struct PrincipalContextPayload {
    environment: String,
    gateway_spiffe_principal: String,
    issuer: String,
    audience: String,
    scope: ResourceScope,
    principal_id: PrincipalUlid,
    session_id: PrincipalSessionUlid,
    grant_id: PrincipalGrantUlid,
    policy_id: AuthorizationPolicyUlid,
    actions: BTreeSet<Action>,
    claims: PrincipalClaims,
    policy_generation: u64,
    issued_at: u64,
    expires_at: u64,
}

impl std::fmt::Debug for PrincipalContextPayload {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.debug_struct("PrincipalContextPayload").finish_non_exhaustive()
    }
}

impl PrincipalContextPayload {
    /// Creates a payload after enforcing identity, action, claim, and lifetime
    /// bounds.
    ///
    /// # Errors
    ///
    /// Returns an authorization error when a text binding is malformed, the
    /// action or claim sets exceed their bounds, or the lifetime is invalid.
    #[allow(clippy::too_many_arguments)]
    pub fn try_new(
        environment: &str,
        gateway_spiffe_principal: &str,
        issuer: &str,
        audience: &str,
        scope: ResourceScope,
        principal_id: PrincipalUlid,
        session_id: PrincipalSessionUlid,
        grant_id: PrincipalGrantUlid,
        policy_id: AuthorizationPolicyUlid,
        actions: BTreeSet<Action>,
        claims: PrincipalClaims,
        policy_generation: u64,
        issued_at: u64,
        expires_at: u64,
    ) -> Result<Self, AuthorizationError> {
        if [environment, gateway_spiffe_principal, issuer, audience]
            .iter()
            .any(|value| !valid_text(value))
        {
            return Err(AuthorizationError::InvalidIdentifier);
        }
        if actions.is_empty() || actions.len() > MAX_PRINCIPAL_ACTIONS {
            return Err(AuthorizationError::ExceedsBound("principal actions"));
        }
        if policy_generation == 0 {
            return Err(AuthorizationError::InvalidGeneration);
        }
        if expires_at <= issued_at || expires_at - issued_at > MAX_CONTEXT_LIFETIME_SECONDS {
            return Err(AuthorizationError::InvalidExpiry);
        }
        let payload = Self {
            environment: environment.to_owned(),
            gateway_spiffe_principal: gateway_spiffe_principal.to_owned(),
            issuer: issuer.to_owned(),
            audience: audience.to_owned(),
            scope,
            principal_id,
            session_id,
            grant_id,
            policy_id,
            actions,
            claims,
            policy_generation,
            issued_at,
            expires_at,
        };
        if payload.canonical_bytes().len() > MAX_CONTEXT_PAYLOAD_BYTES {
            return Err(AuthorizationError::ExceedsBound("principal context payload"));
        }
        Ok(payload)
    }

    /// Returns the canonical payload bytes used as the signature input.
    ///
    /// # Panics
    ///
    /// This method cannot panic for a payload produced by [`Self::try_new`],
    /// because its action and claim counts are bounded there.
    #[must_use]
    pub fn canonical_bytes(&self) -> Vec<u8> {
        let mut bytes = Vec::with_capacity(512);
        bytes.extend_from_slice(&PAYLOAD_MAGIC);
        bytes.push(PAYLOAD_VERSION);
        append_text(&mut bytes, &self.environment);
        append_text(&mut bytes, &self.gateway_spiffe_principal);
        append_text(&mut bytes, &self.issuer);
        append_text(&mut bytes, &self.audience);
        bytes.extend_from_slice(&self.scope.tenant_id().as_bytes());
        bytes.extend_from_slice(&self.scope.project_id().as_bytes());
        bytes.extend_from_slice(&self.scope.database_id().as_bytes());
        bytes.extend_from_slice(&self.principal_id.as_bytes());
        bytes.extend_from_slice(&self.session_id.as_bytes());
        bytes.extend_from_slice(&self.grant_id.as_bytes());
        bytes.extend_from_slice(&self.policy_id.as_bytes());
        bytes.push(u8::try_from(self.actions.len()).expect("action count is bounded"));
        for action in &self.actions {
            bytes.push(action_tag(*action));
        }
        bytes.extend_from_slice(
            &u16::try_from(self.claims.0.len()).expect("claim count is bounded").to_be_bytes(),
        );
        for (name, value) in &self.claims.0 {
            append_text(&mut bytes, name);
            match value {
                super::AttributeValue::Boolean(value) => {
                    bytes.push(0);
                    bytes.push(u8::from(*value));
                }
                super::AttributeValue::Integer(value) => {
                    bytes.push(1);
                    bytes.extend_from_slice(&value.to_be_bytes());
                }
                super::AttributeValue::String(value) => {
                    bytes.push(2);
                    append_text(&mut bytes, value);
                }
            }
        }
        bytes.extend_from_slice(&self.policy_generation.to_be_bytes());
        bytes.extend_from_slice(&self.issued_at.to_be_bytes());
        bytes.extend_from_slice(&self.expires_at.to_be_bytes());
        bytes
    }

    /// Returns the environment binding.
    #[must_use]
    pub fn environment(&self) -> &str {
        &self.environment
    }

    /// Returns the gateway SPIFFE principal binding.
    #[must_use]
    pub fn gateway_spiffe_principal(&self) -> &str {
        &self.gateway_spiffe_principal
    }

    /// Returns the issuer binding.
    #[must_use]
    pub fn issuer(&self) -> &str {
        &self.issuer
    }

    /// Returns the audience binding.
    #[must_use]
    pub fn audience(&self) -> &str {
        &self.audience
    }

    /// Returns the resource scope.
    #[must_use]
    pub const fn scope(&self) -> &ResourceScope {
        &self.scope
    }

    /// Returns the principal identity.
    #[must_use]
    pub const fn principal_id(&self) -> PrincipalUlid {
        self.principal_id
    }

    /// Returns the session identity.
    #[must_use]
    pub const fn session_id(&self) -> PrincipalSessionUlid {
        self.session_id
    }

    /// Returns the grant identity.
    #[must_use]
    pub const fn grant_id(&self) -> PrincipalGrantUlid {
        self.grant_id
    }

    /// Returns the policy identity.
    #[must_use]
    pub const fn policy_id(&self) -> AuthorizationPolicyUlid {
        self.policy_id
    }

    /// Returns the admitted operation classes.
    #[must_use]
    pub fn actions(&self) -> &BTreeSet<Action> {
        &self.actions
    }

    /// Returns the signed claims.
    #[must_use]
    pub const fn claims(&self) -> &PrincipalClaims {
        &self.claims
    }

    /// Returns the authorization generation.
    #[must_use]
    pub const fn policy_generation(&self) -> u64 {
        self.policy_generation
    }

    /// Returns the issuance time in Unix seconds.
    #[must_use]
    pub const fn issued_at(&self) -> u64 {
        self.issued_at
    }

    /// Returns the expiry time in Unix seconds.
    #[must_use]
    pub const fn expires_at(&self) -> u64 {
        self.expires_at
    }
}

/// A signing key used to create test fixtures or an authorized gateway grant.
pub struct PrincipalContextSigner {
    key_id: String,
    private_key: Zeroizing<Vec<u8>>,
    provenance: SigningKeyProvenance,
}

impl PrincipalContextSigner {
    /// Parses a PKCS#8 P-256 private key and records its provenance.
    ///
    /// # Errors
    ///
    /// Returns [`AuthorizationError::ContextSigningFailed`] for an invalid
    /// key or key identifier.
    #[cfg(test)]
    pub(crate) fn from_pkcs8(
        key_id: &str,
        private_key: Vec<u8>,
    ) -> Result<Self, AuthorizationError> {
        if !valid_key_id(key_id) {
            return Err(AuthorizationError::InvalidIdentifier);
        }
        EcdsaKeyPair::from_pkcs8(&ECDSA_P256_SHA256_FIXED_SIGNING, &private_key)
            .map_err(|_| AuthorizationError::ContextSigningFailed)?;
        Ok(Self {
            key_id: key_id.to_owned(),
            private_key: Zeroizing::new(private_key),
            provenance: SigningKeyProvenance::LocalDevelopment,
        })
    }

    /// Returns the key identifier.
    #[must_use]
    pub fn key_id(&self) -> &str {
        &self.key_id
    }

    /// Returns the key provenance.
    #[must_use]
    pub const fn provenance(&self) -> SigningKeyProvenance {
        self.provenance
    }

    /// Returns the public key in uncompressed SEC1 form.
    ///
    /// # Errors
    ///
    /// Returns [`AuthorizationError::ContextSigningFailed`] if the private
    /// key cannot be parsed.
    pub fn public_key(&self) -> Result<Vec<u8>, AuthorizationError> {
        let pair = EcdsaKeyPair::from_pkcs8(&ECDSA_P256_SHA256_FIXED_SIGNING, &self.private_key)
            .map_err(|_| AuthorizationError::ContextSigningFailed)?;
        Ok(pair.public_key().as_ref().to_vec())
    }

    /// Signs a canonical payload and returns its bounded envelope.
    ///
    /// # Errors
    ///
    /// Returns an error when the selected crypto mode rejects the key
    /// provenance or AWS-LC cannot sign the payload.
    pub fn sign(
        &self,
        payload: &PrincipalContextPayload,
        mode: AuthorizationCryptoMode,
    ) -> Result<PrincipalContextEnvelope, AuthorizationError> {
        ensure_signing_mode(mode, self.provenance)?;
        let pair = EcdsaKeyPair::from_pkcs8(&ECDSA_P256_SHA256_FIXED_SIGNING, &self.private_key)
            .map_err(|_| AuthorizationError::ContextSigningFailed)?;
        let signature = pair
            .sign(&SystemRandom::new(), &payload.canonical_bytes())
            .map_err(|_| AuthorizationError::ContextSigningFailed)?;
        PrincipalContextEnvelope::new(
            self.key_id.clone(),
            payload.canonical_bytes(),
            signature.as_ref().to_vec(),
        )
    }
}

impl std::fmt::Debug for PrincipalContextSigner {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter
            .debug_struct("PrincipalContextSigner")
            .field("key_id", &self.key_id)
            .field("provenance", &self.provenance)
            .finish_non_exhaustive()
    }
}

/// One trusted public key in the gateway key set.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct PrincipalContextKey {
    key_id: String,
    public_key: Vec<u8>,
    provenance: SigningKeyProvenance,
}

impl PrincipalContextKey {
    /// Creates a trusted P-256 public key entry.
    ///
    /// # Errors
    ///
    /// Returns an error for an invalid key identifier or public-key shape.
    pub fn new(key_id: &str, public_key: Vec<u8>) -> Result<Self, AuthorizationError> {
        if !valid_key_id(key_id) || public_key.len() != 65 || public_key.first() != Some(&4) {
            return Err(AuthorizationError::InvalidIdentifier);
        }
        Ok(Self {
            key_id: key_id.to_owned(),
            public_key,
            provenance: SigningKeyProvenance::LocalDevelopment,
        })
    }

    #[allow(dead_code, reason = "reserved for the Orchestrator-owned production adapter")]
    pub(crate) fn from_authority(
        key_id: &str,
        public_key: Vec<u8>,
    ) -> Result<Self, AuthorizationError> {
        if !valid_key_id(key_id) || public_key.len() != 65 || public_key.first() != Some(&4) {
            return Err(AuthorizationError::InvalidIdentifier);
        }
        Ok(Self {
            key_id: key_id.to_owned(),
            public_key,
            provenance: SigningKeyProvenance::Production,
        })
    }

    /// Returns the key identifier.
    #[must_use]
    pub fn key_id(&self) -> &str {
        &self.key_id
    }

    /// Returns the public key bytes.
    #[must_use]
    pub fn public_key(&self) -> &[u8] {
        &self.public_key
    }

    /// Returns the key provenance.
    #[must_use]
    pub const fn provenance(&self) -> SigningKeyProvenance {
        self.provenance
    }
}

/// A bounded trusted set of rotating gateway verification keys.
#[derive(Clone, Debug, Default, Eq, PartialEq)]
pub struct PrincipalContextKeySet(BTreeMap<String, PrincipalContextKey>);

impl PrincipalContextKeySet {
    /// Builds a key set and refuses duplicate key identifiers.
    ///
    /// # Errors
    ///
    /// Returns [`AuthorizationError::DuplicateContextKey`] for duplicates.
    pub fn try_new<I>(keys: I) -> Result<Self, AuthorizationError>
    where
        I: IntoIterator<Item = PrincipalContextKey>,
    {
        let mut set = BTreeMap::new();
        for key in keys {
            if set.insert(key.key_id.clone(), key).is_some() {
                return Err(AuthorizationError::DuplicateContextKey);
            }
        }
        Ok(Self(set))
    }

    /// Returns the number of trusted keys.
    #[must_use]
    pub fn len(&self) -> usize {
        self.0.len()
    }

    /// Returns whether no keys are trusted.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.0.is_empty()
    }
}

/// A signed, bounded principal context envelope.
#[derive(Clone, Eq, PartialEq)]
pub struct PrincipalContextEnvelope {
    key_id: String,
    payload: Vec<u8>,
    signature: Vec<u8>,
}

impl PrincipalContextEnvelope {
    fn new(
        key_id: String,
        payload: Vec<u8>,
        signature: Vec<u8>,
    ) -> Result<Self, AuthorizationError> {
        if !valid_key_id(&key_id)
            || payload.is_empty()
            || payload.len() > MAX_CONTEXT_PAYLOAD_BYTES
            || signature.len() != SIGNATURE_BYTES
        {
            return Err(AuthorizationError::MalformedContextEnvelope);
        }
        Ok(Self { key_id, payload, signature })
    }

    /// Parses a complete envelope without decoding or trusting its payload.
    /// Signature verification is performed by [`PrincipalContextVerifier`].
    ///
    /// # Errors
    ///
    /// Returns [`AuthorizationError::MalformedContextEnvelope`] for any
    /// truncated, oversized, non-versioned, or trailing input.
    pub fn from_bytes(bytes: &[u8]) -> Result<Self, AuthorizationError> {
        if bytes.len() > MAX_CONTEXT_ENVELOPE_BYTES || bytes.len() < 15 {
            return Err(AuthorizationError::MalformedContextEnvelope);
        }
        if bytes[..4] != ENVELOPE_MAGIC || bytes[4] != ENVELOPE_VERSION {
            return Err(AuthorizationError::MalformedContextEnvelope);
        }
        let key_len = usize::from(bytes[5]);
        let payload_len = usize::try_from(u32::from_be_bytes(
            bytes[6..10].try_into().map_err(|_| AuthorizationError::MalformedContextEnvelope)?,
        ))
        .map_err(|_| AuthorizationError::MalformedContextEnvelope)?;
        let signature_len = usize::from(u16::from_be_bytes(
            bytes[10..12].try_into().map_err(|_| AuthorizationError::MalformedContextEnvelope)?,
        ));
        let key_end =
            12usize.checked_add(key_len).ok_or(AuthorizationError::MalformedContextEnvelope)?;
        let payload_end =
            key_end.checked_add(payload_len).ok_or(AuthorizationError::MalformedContextEnvelope)?;
        let signature_end = payload_end
            .checked_add(signature_len)
            .ok_or(AuthorizationError::MalformedContextEnvelope)?;
        if signature_end != bytes.len()
            || key_len == 0
            || key_len > MAX_KEY_ID_BYTES
            || payload_len == 0
            || payload_len > MAX_CONTEXT_PAYLOAD_BYTES
            || signature_len != SIGNATURE_BYTES
        {
            return Err(AuthorizationError::MalformedContextEnvelope);
        }
        let key_id = std::str::from_utf8(&bytes[12..key_end])
            .map_err(|_| AuthorizationError::MalformedContextEnvelope)?;
        if !valid_key_id(key_id) {
            return Err(AuthorizationError::MalformedContextEnvelope);
        }
        Self::new(
            key_id.to_owned(),
            bytes[key_end..payload_end].to_vec(),
            bytes[payload_end..signature_end].to_vec(),
        )
    }

    /// Returns the canonical envelope bytes.
    ///
    /// # Panics
    ///
    /// This method cannot panic for an envelope produced by this module,
    /// because its key, payload, and signature lengths are bounded.
    #[must_use]
    pub fn as_bytes(&self) -> Vec<u8> {
        let mut bytes =
            Vec::with_capacity(12 + self.key_id.len() + self.payload.len() + self.signature.len());
        bytes.extend_from_slice(&ENVELOPE_MAGIC);
        bytes.push(ENVELOPE_VERSION);
        bytes.push(u8::try_from(self.key_id.len()).expect("key id is bounded"));
        bytes.extend_from_slice(
            &(u32::try_from(self.payload.len()).expect("payload is bounded")).to_be_bytes(),
        );
        bytes.extend_from_slice(
            &(u16::try_from(self.signature.len()).expect("signature is bounded")).to_be_bytes(),
        );
        bytes.extend_from_slice(self.key_id.as_bytes());
        bytes.extend_from_slice(&self.payload);
        bytes.extend_from_slice(&self.signature);
        bytes
    }

    /// Returns the trusted-key lookup identifier.
    #[must_use]
    pub fn key_id(&self) -> &str {
        &self.key_id
    }
}

impl std::fmt::Debug for PrincipalContextEnvelope {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter
            .debug_struct("PrincipalContextEnvelope")
            .field("key_id", &self.key_id)
            .field("payload_bytes", &self.payload.len())
            .field("signature_bytes", &self.signature.len())
            .finish_non_exhaustive()
    }
}

/// Bounded replay state for one channel-binding owner boundary.
#[derive(Debug, Default)]
pub struct PrincipalContextReplayGuard {
    seen: BTreeMap<PrincipalGrantUlid, u64>,
}

impl PrincipalContextReplayGuard {
    /// Creates an empty replay guard.
    #[must_use]
    pub fn new() -> Self {
        Self::default()
    }

    /// Returns the number of accepted grant/channel pairs.
    #[must_use]
    pub fn len(&self) -> usize {
        self.seen.len()
    }

    /// Returns whether the guard has no accepted grant/channel pairs.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.seen.is_empty()
    }

    /// Removes replay entries whose signed grants have expired.
    pub fn prune_expired(&mut self, now: u64) {
        self.seen.retain(|_, expires_at| *expires_at > now);
    }

    #[cfg(test)]
    pub(crate) fn insert_for_test(&mut self, grant_id: PrincipalGrantUlid, expires_at: u64) {
        self.seen.insert(grant_id, expires_at);
    }
}

/// Verifies signed gateway contexts against one exact service binding.
#[derive(Clone, Debug)]
pub struct PrincipalContextVerifier {
    mode: AuthorizationCryptoMode,
    environment: String,
    gateway_spiffe_principal: String,
    issuer: String,
    audience: String,
    keys: PrincipalContextKeySet,
}

impl PrincipalContextVerifier {
    /// Creates a verifier with exact environment, gateway, issuer, and
    /// audience bindings.
    ///
    /// # Errors
    ///
    /// Returns [`AuthorizationError::InvalidIdentifier`] for malformed
    /// bindings, or [`AuthorizationError::DevelopmentSigningKey`] when a
    /// local key is present in production mode.
    pub fn new(
        mode: AuthorizationCryptoMode,
        environment: &str,
        gateway_spiffe_principal: &str,
        issuer: &str,
        audience: &str,
        keys: PrincipalContextKeySet,
    ) -> Result<Self, AuthorizationError> {
        if [environment, gateway_spiffe_principal, issuer, audience]
            .iter()
            .any(|value| !valid_text(value))
        {
            return Err(AuthorizationError::InvalidIdentifier);
        }
        if mode.is_production()
            && keys.0.values().any(|key| key.provenance == SigningKeyProvenance::LocalDevelopment)
        {
            return Err(AuthorizationError::DevelopmentSigningKey);
        }
        Ok(Self {
            mode,
            environment: environment.to_owned(),
            gateway_spiffe_principal: gateway_spiffe_principal.to_owned(),
            issuer: issuer.to_owned(),
            audience: audience.to_owned(),
            keys,
        })
    }

    /// Verifies a context's signature and exact bindings.
    ///
    /// This method does not apply replay policy. Call
    /// [`Self::verify_with_replay`] at a channel/session boundary.
    ///
    /// # Errors
    ///
    /// Returns a typed refusal for an unknown key, invalid signature,
    /// malformed/noncanonical payload, wrong binding, or invalid time.
    pub fn verify(
        &self,
        envelope: &PrincipalContextEnvelope,
        now: u64,
    ) -> Result<PrincipalContext, AuthorizationError> {
        let key =
            self.keys.0.get(envelope.key_id()).ok_or(AuthorizationError::UnknownContextKey)?;
        if self.mode.is_production() {
            if key.provenance != SigningKeyProvenance::Production {
                return Err(AuthorizationError::DevelopmentSigningKey);
            }
            if aws_lc_rs::try_fips_mode().is_err() {
                return Err(AuthorizationError::ContextSigningFailed);
            }
        }
        UnparsedPublicKey::new(&ECDSA_P256_SHA256_FIXED, key.public_key.as_slice())
            .verify(&envelope.payload, &envelope.signature)
            .map_err(|_| AuthorizationError::InvalidContextSignature)?;
        let payload = decode_payload(&envelope.payload)?;
        if payload.environment != self.environment
            || payload.gateway_spiffe_principal != self.gateway_spiffe_principal
            || payload.issuer != self.issuer
            || payload.audience != self.audience
        {
            return Err(AuthorizationError::ContextBindingMismatch);
        }
        if now < payload.issued_at {
            return Err(AuthorizationError::ContextNotYetValid);
        }
        if now >= payload.expires_at {
            return Err(AuthorizationError::ContextExpired);
        }
        PrincipalContext::from_verified(VerifiedPrincipalInput {
            scope: payload.scope,
            environment: payload.environment,
            gateway_spiffe_principal: payload.gateway_spiffe_principal,
            issuer: payload.issuer,
            principal_id: payload.principal_id,
            session_id: payload.session_id,
            grant_id: payload.grant_id,
            policy_id: payload.policy_id,
            actions: payload.actions,
            claims: payload.claims,
            audience: payload.audience,
            issued_at: payload.issued_at,
            expires_at: payload.expires_at,
            policy_generation: payload.policy_generation,
        })
    }

    /// Verifies a context and rejects a repeated grant on the same channel
    /// binding.
    ///
    /// # Errors
    ///
    /// Returns the errors from [`Self::verify`], or a replay/full refusal from
    /// the bounded guard after successful authentication.
    pub fn verify_with_replay(
        &self,
        envelope: &PrincipalContextEnvelope,
        now: u64,
        channel_binding: PrincipalSessionUlid,
        guard: &mut PrincipalContextReplayGuard,
    ) -> Result<PrincipalContext, AuthorizationError> {
        guard.prune_expired(now);
        let context = self.verify(envelope, now)?;
        if context.session_id() != channel_binding {
            return Err(AuthorizationError::ContextBindingMismatch);
        }
        if guard.seen.contains_key(&context.grant_id()) {
            return Err(AuthorizationError::ContextReplay);
        }
        if guard.seen.len() >= MAX_REPLAY_ENTRIES {
            return Err(AuthorizationError::ContextReplayGuardFull);
        }
        guard.seen.insert(context.grant_id(), context.expires_at());
        Ok(context)
    }
}

fn ensure_signing_mode(
    mode: AuthorizationCryptoMode,
    provenance: SigningKeyProvenance,
) -> Result<(), AuthorizationError> {
    if mode.is_production() {
        if provenance != SigningKeyProvenance::Production {
            return Err(AuthorizationError::DevelopmentSigningKey);
        }
        if aws_lc_rs::try_fips_mode().is_err() {
            return Err(AuthorizationError::ContextSigningFailed);
        }
    }
    Ok(())
}

fn valid_text(value: &str) -> bool {
    super::valid_identifier(value) && value.len() <= MAX_TEXT_BYTES
}

fn valid_key_id(value: &str) -> bool {
    valid_text(value) && value.len() <= MAX_KEY_ID_BYTES && value.is_ascii()
}

fn append_text(bytes: &mut Vec<u8>, value: &str) {
    bytes.extend_from_slice(&(u16::try_from(value.len()).expect("text is bounded")).to_be_bytes());
    bytes.extend_from_slice(value.as_bytes());
}

fn action_tag(action: Action) -> u8 {
    match action {
        Action::Read => 0,
        Action::Query => 1,
        Action::Mutate => 2,
        Action::Subscribe => 3,
        Action::Schema => 4,
        Action::Admin => 5,
    }
}

fn decode_payload(bytes: &[u8]) -> Result<PrincipalContextPayload, AuthorizationError> {
    if bytes.len() > MAX_CONTEXT_PAYLOAD_BYTES
        || bytes.len() < 5
        || bytes[..4] != PAYLOAD_MAGIC
        || bytes[4] != PAYLOAD_VERSION
    {
        return Err(AuthorizationError::InvalidContextPayload);
    }
    let mut cursor = Cursor { bytes, offset: 5 };
    let environment = cursor.text()?;
    let gateway = cursor.text()?;
    let issuer = cursor.text()?;
    let audience = cursor.text()?;
    let tenant = TenantUlid::from_wire_bytes(cursor.identity()?);
    let project = ProjectUlid::from_wire_bytes(cursor.identity()?);
    let database = DatabaseUlid::from_wire_bytes(cursor.identity()?);
    let principal = PrincipalUlid::from_wire_bytes(cursor.identity()?);
    let session = PrincipalSessionUlid::from_wire_bytes(cursor.identity()?);
    let grant = PrincipalGrantUlid::from_wire_bytes(cursor.identity()?);
    let policy = AuthorizationPolicyUlid::from_wire_bytes(cursor.identity()?);
    let action_count = usize::from(cursor.byte()?);
    if action_count == 0 || action_count > MAX_PRINCIPAL_ACTIONS {
        return Err(AuthorizationError::ExceedsBound("principal actions"));
    }
    let mut actions = BTreeSet::new();
    for _ in 0..action_count {
        let action = match cursor.byte()? {
            0 => Action::Read,
            1 => Action::Query,
            2 => Action::Mutate,
            3 => Action::Subscribe,
            4 => Action::Schema,
            5 => Action::Admin,
            _ => return Err(AuthorizationError::InvalidContextPayload),
        };
        if !actions.insert(action) {
            return Err(AuthorizationError::InvalidContextPayload);
        }
    }
    let claim_count = usize::from(cursor.u16()?);
    if claim_count > MAX_PRINCIPAL_CLAIMS {
        return Err(AuthorizationError::ExceedsBound("principal claims"));
    }
    let mut claims = BTreeMap::new();
    for _ in 0..claim_count {
        let name = cursor.text()?.to_owned();
        let value = match cursor.byte()? {
            0 => super::AttributeValue::Boolean(cursor.byte()? != 0),
            1 => super::AttributeValue::Integer(cursor.i64()?),
            2 => super::AttributeValue::String(cursor.text()?.to_owned()),
            _ => return Err(AuthorizationError::InvalidContextPayload),
        };
        if claims.insert(name, value).is_some() {
            return Err(AuthorizationError::InvalidContextPayload);
        }
    }
    let generation = cursor.u64()?;
    let issued_at = cursor.u64()?;
    let expires_at = cursor.u64()?;
    if cursor.offset != bytes.len() {
        return Err(AuthorizationError::InvalidContextPayload);
    }
    let payload = PrincipalContextPayload::try_new(
        environment,
        gateway,
        issuer,
        audience,
        ResourceScope::try_new(tenant, project, database)?,
        principal,
        session,
        grant,
        policy,
        actions,
        PrincipalClaims::try_new(claims)?,
        generation,
        issued_at,
        expires_at,
    )?;
    if payload.canonical_bytes() != bytes {
        return Err(AuthorizationError::InvalidContextPayload);
    }
    Ok(payload)
}

struct Cursor<'a> {
    bytes: &'a [u8],
    offset: usize,
}

impl<'a> Cursor<'a> {
    fn take(&mut self, len: usize) -> Result<&'a [u8], AuthorizationError> {
        let end = self.offset.checked_add(len).ok_or(AuthorizationError::InvalidContextPayload)?;
        let value =
            self.bytes.get(self.offset..end).ok_or(AuthorizationError::InvalidContextPayload)?;
        self.offset = end;
        Ok(value)
    }

    fn byte(&mut self) -> Result<u8, AuthorizationError> {
        Ok(*self.take(1)?.first().ok_or(AuthorizationError::InvalidContextPayload)?)
    }

    fn u16(&mut self) -> Result<u16, AuthorizationError> {
        Ok(u16::from_be_bytes(
            self.take(2)?.try_into().map_err(|_| AuthorizationError::InvalidContextPayload)?,
        ))
    }

    fn u64(&mut self) -> Result<u64, AuthorizationError> {
        Ok(u64::from_be_bytes(
            self.take(8)?.try_into().map_err(|_| AuthorizationError::InvalidContextPayload)?,
        ))
    }

    fn i64(&mut self) -> Result<i64, AuthorizationError> {
        Ok(i64::from_be_bytes(
            self.take(8)?.try_into().map_err(|_| AuthorizationError::InvalidContextPayload)?,
        ))
    }

    fn identity(&mut self) -> Result<[u8; ULID_BINARY_BYTES], AuthorizationError> {
        self.take(ULID_BINARY_BYTES)?
            .try_into()
            .map_err(|_| AuthorizationError::InvalidContextPayload)
    }

    fn text(&mut self) -> Result<&'a str, AuthorizationError> {
        let len = usize::from(self.u16()?);
        let bytes = self.take(len)?;
        let value =
            std::str::from_utf8(bytes).map_err(|_| AuthorizationError::InvalidContextPayload)?;
        if !valid_text(value) {
            return Err(AuthorizationError::InvalidContextPayload);
        }
        Ok(value)
    }
}

impl Drop for PrincipalContextSigner {
    fn drop(&mut self) {
        self.private_key.zeroize();
    }
}
