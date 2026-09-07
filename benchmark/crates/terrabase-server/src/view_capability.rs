//! Issuance and verification of the reactive view capability (D-14/D-55).
//!
//! A capability is deliberately an opaque, deterministic HTTP-header value:
//!
//! ```text
//! v1.<view>.<generation>.<scope>.<entitlement-epoch>.<region>.<expiry>.<hmac>
//! ```
//!
//! The HMAC covers every field before the final dot.  `scope` is kept in the
//! established readable form when it contains only safe field characters and
//! is otherwise encoded as `b64:<base64url-without-padding>`.  The Region is
//! part of the authenticated value even though the edge binds the token to
//! the view and generation from the request path.  A current and previous key
//! can be held by the verifier during the orchestrator's 24-hour rotation.
//!
//! This module intentionally has no logging and capability errors never carry
//! token, scope, key, or signature material.

#![allow(clippy::unreadable_literal)]

use std::fmt;
use std::time::{SystemTime, UNIX_EPOCH};

use base64::Engine as _;
use terrabase::regions::RegionCode;

/// The fixed header used by reactive view log and snapshot requests.
pub const VIEW_CAPABILITY_HEADER: &str = "x-terrascale-view-capability";

/// Alias retained for transport code that calls the value simply a capability.
pub const CAPABILITY_HEADER: &str = VIEW_CAPABILITY_HEADER;

/// The header spelling used by the Cloudflare Snippet contract.
pub const EDGE_CAPABILITY_HEADER: &str = "x-terrascale-capability";

/// Capability wire version.
pub const CAPABILITY_VERSION: &str = "v1";

/// The expiry bucket width required by D-14, in seconds.
pub const EXPIRY_BUCKET_SECONDS: u64 = 600;

/// The acceptance overlap: the mint bucket and exactly one preceding bucket.
pub const CAPABILITY_OVERLAP_SECONDS: u64 = EXPIRY_BUCKET_SECONDS * 2;

const MAX_CAPABILITY_BYTES: usize = 1024;
const MAX_VIEW_BYTES: usize = 128;
const MAX_SCOPE_BYTES: usize = 256;
const MAX_KEY_BYTES: usize = 4096;
const MIN_KEY_BYTES: usize = 16;
const MAC_HEX_BYTES: usize = 64;

/// Safe, typed claims that are shared by an issuer and a verifier.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ViewCapabilityClaims {
    /// The canonical shared-view identity.
    pub view: String,
    /// The view-generation identity.
    pub generation: u64,
    /// The canonical authorization scope used to build the shared view.
    pub authorization_scope: String,
    /// The entitlement epoch captured when the view was attached.
    pub entitlement_epoch: u64,
    /// The Region whose member issued and serves the view.
    pub region: RegionCode,
    /// The exclusive expiry instant, aligned to a ten-minute boundary.
    pub expiry: u64,
}

impl ViewCapabilityClaims {
    /// Builds claims before an expiry has been selected by an issuer.
    #[must_use]
    pub fn new(
        view: &str,
        generation: u64,
        authorization_scope: &str,
        entitlement_epoch: u64,
        region: RegionCode,
    ) -> Self {
        Self {
            view: view.to_owned(),
            generation,
            authorization_scope: authorization_scope.to_owned(),
            entitlement_epoch,
            region,
            expiry: 0,
        }
    }

    fn validate(&self) -> Result<(), CapabilityError> {
        validate_view(&self.view)?;
        validate_scope(&self.authorization_scope)?;
        if self.expiry != 0 {
            validate_expiry(self.expiry)?;
        }
        Ok(())
    }
}

/// A capability returned by a successful verification.
pub type ViewCapability = ViewCapabilityClaims;

/// Key material was not suitable for capability HMAC use.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum CapabilityKeyError {
    /// A key was shorter than the minimum cryptographic bound.
    TooShort,
    /// A key exceeded the hard memory bound.
    TooLong,
    /// The current and previous key were identical.
    Duplicate,
}

impl fmt::Display for CapabilityKeyError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(match self {
            Self::TooShort => "capability key is too short",
            Self::TooLong => "capability key is too long",
            Self::Duplicate => "capability keys must differ",
        })
    }
}

impl std::error::Error for CapabilityKeyError {}

/// A capability input, token, or clock failed the fixed contract.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum CapabilityError {
    /// The capability or one of its claims was malformed or out of bounds.
    Malformed,
    /// The token's view or generation did not match the requested route.
    RouteMismatch,
    /// The token is outside its half-open validity interval.
    Expired,
    /// The token's MAC did not verify against either configured key.
    InvalidSignature,
    /// The platform clock could not produce a UNIX timestamp.
    Clock,
}

impl fmt::Display for CapabilityError {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.write_str(match self {
            Self::Malformed => "view capability is malformed",
            Self::RouteMismatch => "view capability does not match this view",
            Self::Expired => "view capability expired",
            Self::InvalidSignature => "view capability signature is invalid",
            Self::Clock => "capability clock is unavailable",
        })
    }
}

impl std::error::Error for CapabilityError {}

/// Issues deterministic capabilities with one current HMAC key.
pub struct ViewCapabilityIssuer {
    current_key: Vec<u8>,
}

impl ViewCapabilityIssuer {
    /// Creates an issuer. The key is copied so the caller may drop its buffer.
    ///
    /// # Errors
    ///
    /// Returns a key-bound error when the key is outside the hard bounds.
    pub fn new(current_key: impl AsRef<[u8]>) -> Result<Self, CapabilityKeyError> {
        let current_key = current_key.as_ref();
        validate_key(current_key)?;
        Ok(Self { current_key: current_key.to_vec() })
    }

    /// Issues a token using a UNIX-seconds clock supplied by the caller.
    ///
    /// The expiry is `(current bucket + 2) * 600`, making the token valid in
    /// its mint bucket and the preceding bucket only at verification time.
    ///
    /// # Errors
    ///
    /// Returns [`CapabilityError::Malformed`] when a claim is out of bounds.
    pub fn issue(
        &self,
        view: &str,
        generation: u64,
        authorization_scope: &str,
        entitlement_epoch: u64,
        region: RegionCode,
        now: u64,
    ) -> Result<String, CapabilityError> {
        self.issue_claims(
            ViewCapabilityClaims::new(
                view,
                generation,
                authorization_scope,
                entitlement_epoch,
                region,
            ),
            now,
        )
    }

    /// Issues a token from claims, selecting its deterministic expiry bucket.
    ///
    /// # Errors
    ///
    /// Returns [`CapabilityError::Malformed`] when a claim or timestamp
    /// cannot be represented by the fixed wire contract.
    pub fn issue_claims(
        &self,
        mut claims: ViewCapabilityClaims,
        now: u64,
    ) -> Result<String, CapabilityError> {
        claims.validate()?;
        let bucket = now / EXPIRY_BUCKET_SECONDS;
        claims.expiry = bucket
            .checked_add(2)
            .and_then(|value| value.checked_mul(EXPIRY_BUCKET_SECONDS))
            .ok_or(CapabilityError::Malformed)?;
        encode_and_sign(&claims, &self.current_key)
    }

    /// Issues a capability using the host UNIX clock.
    ///
    /// # Errors
    ///
    /// Returns [`CapabilityError::Clock`] when the host clock predates UNIX
    /// epoch, or [`CapabilityError::Malformed`] for invalid claims.
    pub fn issue_now(
        &self,
        view: &str,
        generation: u64,
        authorization_scope: &str,
        entitlement_epoch: u64,
        region: RegionCode,
    ) -> Result<String, CapabilityError> {
        self.issue(view, generation, authorization_scope, entitlement_epoch, region, unix_now()?)
    }
}

impl fmt::Debug for ViewCapabilityIssuer {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter.debug_struct("ViewCapabilityIssuer").field("key", &"[redacted]").finish()
    }
}

/// Verifies capabilities with the current key and, during rotation, one
/// previous key. A verifier never exposes either key in formatting or errors.
pub struct ViewCapabilityVerifier {
    current_key: Vec<u8>,
    previous_key: Option<Vec<u8>>,
}

impl ViewCapabilityVerifier {
    /// Creates a verifier without a rotation overlap key.
    ///
    /// # Errors
    ///
    /// Returns a key-bound error when the key is outside the hard bounds.
    pub fn new(current_key: impl AsRef<[u8]>) -> Result<Self, CapabilityKeyError> {
        let current_key = current_key.as_ref();
        validate_key(current_key)?;
        Ok(Self { current_key: current_key.to_vec(), previous_key: None })
    }

    /// Creates a verifier accepting the current and previous rotation keys.
    ///
    /// # Errors
    ///
    /// Returns a key-bound error when either key is outside the hard bounds
    /// or when both keys are identical.
    pub fn with_previous(
        current_key: impl AsRef<[u8]>,
        previous_key: impl AsRef<[u8]>,
    ) -> Result<Self, CapabilityKeyError> {
        let current_key = current_key.as_ref();
        let previous_key = previous_key.as_ref();
        validate_key(current_key)?;
        validate_key(previous_key)?;
        if current_key == previous_key {
            return Err(CapabilityKeyError::Duplicate);
        }
        Ok(Self { current_key: current_key.to_vec(), previous_key: Some(previous_key.to_vec()) })
    }

    /// Verifies a token against the route's view and generation.
    ///
    /// # Errors
    ///
    /// Returns a typed refusal for malformed, expired, foreign, or
    /// incorrectly signed capabilities.
    pub fn verify(
        &self,
        token: &str,
        view: &str,
        generation: u64,
        now: u64,
    ) -> Result<ViewCapability, CapabilityError> {
        validate_view(view)?;
        let claims = decode(token)?;
        if claims.view != view || claims.generation != generation {
            return Err(CapabilityError::RouteMismatch);
        }
        verify_time(claims.expiry, now)?;
        let body = body_for(&claims)?;
        let presented = decode_mac(token)?;
        let expected = hmac_sha256(&self.current_key, body.as_bytes());
        let previous = self.previous_key.as_ref().map(|key| hmac_sha256(key, body.as_bytes()));
        let current_ok = constant_time_equal(&expected, &presented);
        let previous_ok = previous.as_ref().is_some_and(|mac| constant_time_equal(mac, &presented));
        if !current_ok && !previous_ok {
            return Err(CapabilityError::InvalidSignature);
        }
        Ok(claims)
    }

    /// Verifies a token against every authorization claim as well as its route.
    ///
    /// # Errors
    ///
    /// Returns a typed refusal when the token, expected claims, or signature
    /// does not satisfy the fixed contract.
    pub fn verify_claims(
        &self,
        token: &str,
        expected: &ViewCapabilityClaims,
        now: u64,
    ) -> Result<ViewCapability, CapabilityError> {
        expected.validate()?;
        let claims = self.verify(token, &expected.view, expected.generation, now)?;
        if claims.authorization_scope != expected.authorization_scope
            && !claims
                .authorization_scope
                .strip_prefix(&format!("{}:", expected.authorization_scope))
                .is_some_and(|scope| {
                    crate::membership_revocations::valid_scope(&expected.authorization_scope)
                        && crate::membership_revocations::valid_authority_scopes(scope)
                })
            || claims.entitlement_epoch != expected.entitlement_epoch
            || claims.region != expected.region
        {
            return Err(CapabilityError::RouteMismatch);
        }
        Ok(claims)
    }

    /// Verifies a capability using the host UNIX clock.
    ///
    /// # Errors
    ///
    /// Returns [`CapabilityError::Clock`] when the host clock predates UNIX
    /// epoch, or the verification refusal for the token.
    pub fn verify_now(
        &self,
        token: &str,
        view: &str,
        generation: u64,
    ) -> Result<ViewCapability, CapabilityError> {
        self.verify(token, view, generation, unix_now()?)
    }
}

impl fmt::Debug for ViewCapabilityVerifier {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter
            .debug_struct("ViewCapabilityVerifier")
            .field("current_key", &"[redacted]")
            .field("previous_key", &self.previous_key.as_ref().map(|_| "[redacted]"))
            .finish()
    }
}

/// Compatibility alias for the reactive server's shorter naming convention.
pub type CapabilityIssuer = ViewCapabilityIssuer;

/// Compatibility alias for the reactive server's shorter naming convention.
pub type CapabilityVerifier = ViewCapabilityVerifier;

fn validate_key(key: &[u8]) -> Result<(), CapabilityKeyError> {
    if key.len() < MIN_KEY_BYTES {
        return Err(CapabilityKeyError::TooShort);
    }
    if key.len() > MAX_KEY_BYTES {
        return Err(CapabilityKeyError::TooLong);
    }
    Ok(())
}

fn validate_view(view: &str) -> Result<(), CapabilityError> {
    if view.is_empty() || view.len() > MAX_VIEW_BYTES {
        return Err(CapabilityError::Malformed);
    }
    if !view.bytes().all(|byte| byte.is_ascii_alphanumeric() || byte == b'_' || byte == b'-') {
        return Err(CapabilityError::Malformed);
    }
    Ok(())
}

fn validate_scope(scope: &str) -> Result<(), CapabilityError> {
    if scope.is_empty() || scope.len() > MAX_SCOPE_BYTES {
        return Err(CapabilityError::Malformed);
    }
    Ok(())
}

fn validate_expiry(expiry: u64) -> Result<(), CapabilityError> {
    if expiry == 0 || !expiry.is_multiple_of(EXPIRY_BUCKET_SECONDS) {
        return Err(CapabilityError::Malformed);
    }
    Ok(())
}

fn verify_time(expiry: u64, now: u64) -> Result<(), CapabilityError> {
    validate_expiry(expiry)?;
    if now >= expiry || now < expiry.saturating_sub(CAPABILITY_OVERLAP_SECONDS) {
        return Err(CapabilityError::Expired);
    }
    Ok(())
}

fn encode_and_sign(claims: &ViewCapabilityClaims, key: &[u8]) -> Result<String, CapabilityError> {
    let body = body_for(claims)?;
    let mac = hmac_sha256(key, body.as_bytes());
    let token = format!("{body}.{}", hex::encode(mac));
    if token.len() > MAX_CAPABILITY_BYTES {
        return Err(CapabilityError::Malformed);
    }
    Ok(token)
}

fn body_for(claims: &ViewCapabilityClaims) -> Result<String, CapabilityError> {
    claims.validate()?;
    let scope = encode_scope(&claims.authorization_scope);
    let body = format!(
        "{CAPABILITY_VERSION}.{}.{}.{}.{}.{}.{}",
        claims.view,
        claims.generation,
        scope,
        claims.entitlement_epoch,
        claims.region,
        claims.expiry
    );
    if body.len() > MAX_CAPABILITY_BYTES - MAC_HEX_BYTES - 1 {
        return Err(CapabilityError::Malformed);
    }
    Ok(body)
}

fn decode(token: &str) -> Result<ViewCapabilityClaims, CapabilityError> {
    if token.len() > MAX_CAPABILITY_BYTES
        || token.trim() != token
        || token.contains([',', ' ', '\t', '\r', '\n'])
    {
        return Err(CapabilityError::Malformed);
    }
    let fields = token.split('.').collect::<Vec<_>>();
    if fields.len() != 8 || fields[0] != CAPABILITY_VERSION || fields[7].len() != MAC_HEX_BYTES {
        return Err(CapabilityError::Malformed);
    }
    validate_view(fields[1])?;
    let generation = parse_decimal(fields[2])?;
    let scope = decode_scope(fields[3])?;
    let entitlement_epoch = parse_decimal(fields[4])?;
    let region = RegionCode::from_text(fields[5]).map_err(|_| CapabilityError::Malformed)?;
    let expiry = parse_decimal(fields[6])?;
    validate_expiry(expiry)?;
    if !fields[7].bytes().all(|byte| byte.is_ascii_hexdigit() && !byte.is_ascii_uppercase()) {
        return Err(CapabilityError::Malformed);
    }
    Ok(ViewCapabilityClaims {
        view: fields[1].to_owned(),
        generation,
        authorization_scope: scope,
        entitlement_epoch,
        region,
        expiry,
    })
}

fn decode_mac(token: &str) -> Result<[u8; 32], CapabilityError> {
    let field = token.rsplit('.').next().ok_or(CapabilityError::Malformed)?;
    let mut bytes = [0; 32];
    hex::decode_to_slice(field, &mut bytes).map_err(|_| CapabilityError::Malformed)?;
    Ok(bytes)
}

fn parse_decimal(value: &str) -> Result<u64, CapabilityError> {
    if value.is_empty() || (value.len() > 1 && value.starts_with('0')) || value.len() > 20 {
        return Err(CapabilityError::Malformed);
    }
    value.parse::<u64>().map_err(|_| CapabilityError::Malformed)
}

fn encode_scope(scope: &str) -> String {
    if !scope.starts_with("b64:")
        && scope
            .bytes()
            .all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'_' | b'-' | b':'))
    {
        scope.to_owned()
    } else {
        format!("b64:{}", base64::engine::general_purpose::URL_SAFE_NO_PAD.encode(scope.as_bytes()))
    }
}

fn decode_scope(field: &str) -> Result<String, CapabilityError> {
    if let Some(encoded) = field.strip_prefix("b64:") {
        let bytes = base64::engine::general_purpose::URL_SAFE_NO_PAD
            .decode(encoded)
            .map_err(|_| CapabilityError::Malformed)?;
        let scope = String::from_utf8(bytes).map_err(|_| CapabilityError::Malformed)?;
        validate_scope(&scope)?;
        if encode_scope(&scope) != field {
            return Err(CapabilityError::Malformed);
        }
        return Ok(scope);
    }
    if field.bytes().all(|byte| byte.is_ascii_alphanumeric() || matches!(byte, b'_' | b'-' | b':'))
    {
        let scope = field.to_owned();
        validate_scope(&scope)?;
        return Ok(scope);
    }
    Err(CapabilityError::Malformed)
}

fn constant_time_equal(left: &[u8; 32], right: &[u8; 32]) -> bool {
    aws_lc_rs::constant_time::verify_slices_are_equal(left, right).is_ok()
}

fn unix_now() -> Result<u64, CapabilityError> {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .ok()
        .and_then(|duration| duration.as_secs().checked_add(0))
        .ok_or(CapabilityError::Clock)
}

pub(crate) fn hmac_sha256_hex(key: &[u8], message: &[u8]) -> String {
    hex::encode(hmac_sha256(key, message))
}

fn hmac_sha256(key: &[u8], message: &[u8]) -> [u8; 32] {
    let key = aws_lc_rs::hmac::Key::new(aws_lc_rs::hmac::HMAC_SHA256, key);
    let tag = aws_lc_rs::hmac::sign(&key, message);
    let mut output = [0u8; 32];
    output.copy_from_slice(tag.as_ref());
    output
}

#[cfg(test)]
mod tests {
    use super::*;

    const KEY: &[u8] = b"0123456789abcdef0123456789abcdef";
    const PREVIOUS: &[u8] = b"fedcba9876543210fedcba9876543210";
    const VIEW: &str = "01J5G8Z3M2P4Q6R8T0V1W2X4YZ";
    const NOW: u64 = 2931234 * EXPIRY_BUCKET_SECONDS + 1;

    fn issue() -> String {
        ViewCapabilityIssuer::new(KEY)
            .expect("key")
            .issue(VIEW, 7, "rule:v3:db:01J5G8Z3M2P4Q6R8T0V1W2X4YZ", 7, RegionCode::GRU, NOW)
            .expect("capability")
    }

    #[test]
    fn hmac_matches_standard_vectors() {
        assert_eq!(
            hex::encode(hmac_sha256(b"key", b"The quick brown fox jumps over the lazy dog")),
            "f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8"
        );
        // RFC 4231 case 6: keys larger than the SHA-256 block are hashed first.
        assert_eq!(
            hmac_sha256_hex(
                &[0xaa; 131],
                b"Test Using Larger Than Block-Size Key - Hash Key First"
            ),
            "60e431591ee0b67f0d8a26aacbf5b77f8e0bc6213728c5140546040f0ee37f54"
        );
    }

    #[test]
    fn golden_token_is_deterministic_and_verifies() {
        let token = issue();
        assert_eq!(token, issue());
        assert_eq!(
            token,
            "v1.01J5G8Z3M2P4Q6R8T0V1W2X4YZ.7.rule:v3:db:01J5G8Z3M2P4Q6R8T0V1W2X4YZ.7.GRU.1758741600.632af32a9e42ef75c19381558b8fa0e066e7d98f17af7f13e74f72eef4f6ab4c"
        );
        assert!(token.contains(".GRU."));
        let verifier = ViewCapabilityVerifier::new(KEY).expect("key");
        let claims = verifier.verify(&token, VIEW, 7, NOW).expect("valid");
        assert_eq!(claims.authorization_scope, "rule:v3:db:01J5G8Z3M2P4Q6R8T0V1W2X4YZ");
        assert_eq!(claims.region, RegionCode::GRU);
    }

    #[test]
    fn capability_codecs_require_canonical_scope_and_lowercase_mac() {
        assert_eq!(decode_scope("b64:Lw"), Ok("/".into()));
        for malformed in ["b64:", "b64:Lw==", "b64:Lx", "b64:c2NvcGU", "b64:____"] {
            assert_eq!(decode_scope(malformed), Err(CapabilityError::Malformed));
        }
        let token = issue();
        let (body, mac) = token.rsplit_once('.').unwrap();
        let uppercase = format!("{body}.{}", mac.to_uppercase());
        assert_ne!(uppercase, token);
        assert_eq!(decode(&uppercase), Err(CapabilityError::Malformed));
        assert!(decode(&token).is_ok());
    }

    #[test]
    fn opaque_scopes_are_canonical_and_dot_safe() {
        let token = ViewCapabilityIssuer::new(KEY)
            .expect("key")
            .issue(VIEW, 1, "tenant/project.with/slash?role=reader\0", 0, RegionCode::SFO, NOW)
            .expect("capability");
        assert!(token.split('.').nth(3).is_some_and(|field| field.starts_with("b64:")));
        let verifier = ViewCapabilityVerifier::new(KEY).expect("key");
        assert_eq!(verifier.verify(&token, VIEW, 1, NOW).expect("valid").region, RegionCode::SFO);
    }

    #[test]
    fn exactly_one_bucket_overlap_is_enforced() {
        let token = issue();
        let verifier = ViewCapabilityVerifier::new(KEY).expect("key");
        assert!(verifier.verify(&token, VIEW, 7, NOW + EXPIRY_BUCKET_SECONDS * 2 - 2).is_ok());
        assert_eq!(
            verifier.verify(&token, VIEW, 7, NOW + EXPIRY_BUCKET_SECONDS * 2),
            Err(CapabilityError::Expired)
        );
        assert_eq!(verifier.verify(&token, VIEW, 7, NOW - 2), Err(CapabilityError::Expired));
    }

    #[test]
    fn rotation_accepts_previous_but_not_foreign_keys() {
        let token = ViewCapabilityIssuer::new(PREVIOUS)
            .expect("key")
            .issue(VIEW, 1, "scope", 1, RegionCode::FRA, NOW)
            .expect("capability");
        let verifier = ViewCapabilityVerifier::with_previous(KEY, PREVIOUS).expect("keys");
        assert!(verifier.verify(&token, VIEW, 1, NOW).is_ok());
        assert_eq!(
            ViewCapabilityVerifier::new(KEY).expect("key").verify(&token, VIEW, 1, NOW),
            Err(CapabilityError::InvalidSignature)
        );
    }

    #[test]
    fn malformed_tokens_fail_closed_without_secret_material() {
        let verifier = ViewCapabilityVerifier::new(KEY).expect("key");
        for token in [
            "",
            "v2.x.1.scope.0.GRU.1.deadbeef",
            "v1.x.1.scope.0.GRU.1.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaz",
            "v1.x.1.scope.with.dot.0.GRU.1.aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        ] {
            let error = verifier.verify(token, "x", 1, NOW).expect_err("reject");
            assert!(!error.to_string().contains("0123456789"));
        }
        assert!(matches!(ViewCapabilityIssuer::new(b"short"), Err(CapabilityKeyError::TooShort)));
        assert!(matches!(
            ViewCapabilityVerifier::with_previous(KEY, KEY),
            Err(CapabilityKeyError::Duplicate)
        ));
    }
    #[test]
    fn native_membership_feature_vector_matches_edge_fixture() {
        let vector: serde_json::Value =
            serde_json::from_str(include_str!("membership-capability-vector.json")).unwrap();
        let scope = format!(
            "{}:{}:{}",
            vector["databaseScope"].as_str().unwrap(),
            vector["membershipScope"].as_str().unwrap(),
            vector["featureScope"].as_str().unwrap()
        );
        let key = hex::decode(vector["keyHex"].as_str().unwrap()).unwrap();
        let issuer = ViewCapabilityIssuer::new(&key).unwrap();
        let token = issuer
            .issue("shared", 1, &scope, 0, RegionCode::GRU, vector["now"].as_u64().unwrap())
            .unwrap();
        assert_eq!(token, vector["token"].as_str().unwrap());
    }
}
