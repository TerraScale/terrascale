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
    let token = format!("{body}.{}", hex_encode(&mac));
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
    let mut output = [0; 32];
    for (index, pair) in field.as_bytes().chunks_exact(2).enumerate() {
        output[index] = (hex_value(pair[0])? << 4) | hex_value(pair[1])?;
    }
    Ok(output)
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
        format!("b64:{}", base64url_encode(scope.as_bytes()))
    }
}

fn decode_scope(field: &str) -> Result<String, CapabilityError> {
    if let Some(encoded) = field.strip_prefix("b64:") {
        let bytes = base64url_decode(encoded)?;
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

fn base64url_encode(bytes: &[u8]) -> String {
    const TABLE: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
    let mut output = String::with_capacity(bytes.len().div_ceil(3) * 4);
    for chunk in bytes.chunks(3) {
        let a = chunk[0];
        let b = chunk.get(1).copied().unwrap_or(0);
        let c = chunk.get(2).copied().unwrap_or(0);
        output.push(TABLE[usize::from(a >> 2)] as char);
        output.push(TABLE[usize::from((a << 4 | b >> 4) & 0x3f)] as char);
        if chunk.len() > 1 {
            output.push(TABLE[usize::from((b << 2 | c >> 6) & 0x3f)] as char);
        }
        if chunk.len() > 2 {
            output.push(TABLE[usize::from(c & 0x3f)] as char);
        }
    }
    output
}

fn base64url_decode(value: &str) -> Result<Vec<u8>, CapabilityError> {
    if value.is_empty()
        || value.len() % 4 == 1
        || !value.bytes().all(|byte| byte.is_ascii_alphanumeric() || byte == b'_' || byte == b'-')
    {
        return Err(CapabilityError::Malformed);
    }
    let mut output = Vec::with_capacity(value.len() * 3 / 4);
    let bytes = value.as_bytes();
    let mut index = 0;
    while index < bytes.len() {
        let a = base64_value(bytes[index])?;
        let b = base64_value(bytes[index + 1])?;
        output.push((a << 2) | (b >> 4));
        if index + 2 < bytes.len() {
            let c = base64_value(bytes[index + 2])?;
            output.push((b << 4) | (c >> 2));
            if index + 3 < bytes.len() {
                let d = base64_value(bytes[index + 3])?;
                output.push((c << 6) | d);
            }
        }
        index += 4;
    }
    Ok(output)
}

fn base64_value(byte: u8) -> Result<u8, CapabilityError> {
    match byte {
        b'A'..=b'Z' => Ok(byte - b'A'),
        b'a'..=b'z' => Ok(byte - b'a' + 26),
        b'0'..=b'9' => Ok(byte - b'0' + 52),
        b'-' => Ok(62),
        b'_' => Ok(63),
        _ => Err(CapabilityError::Malformed),
    }
}

fn hex_value(byte: u8) -> Result<u8, CapabilityError> {
    match byte {
        b'0'..=b'9' => Ok(byte - b'0'),
        b'a'..=b'f' => Ok(byte - b'a' + 10),
        _ => Err(CapabilityError::Malformed),
    }
}

fn hex_encode(bytes: &[u8; 32]) -> String {
    const TABLE: &[u8; 16] = b"0123456789abcdef";
    let mut output = String::with_capacity(64);
    for &byte in bytes {
        output.push(TABLE[usize::from(byte >> 4)] as char);
        output.push(TABLE[usize::from(byte & 0x0f)] as char);
    }
    output
}

fn constant_time_equal(left: &[u8; 32], right: &[u8; 32]) -> bool {
    let mut difference = 0u8;
    for (&a, &b) in left.iter().zip(right) {
        difference |= a ^ b;
    }
    difference == 0
}

fn unix_now() -> Result<u64, CapabilityError> {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .ok()
        .and_then(|duration| duration.as_secs().checked_add(0))
        .ok_or(CapabilityError::Clock)
}

fn hmac_sha256(key: &[u8], message: &[u8]) -> [u8; 32] {
    let mut key_block = [0u8; 64];
    if key.len() > key_block.len() {
        key_block[..32].copy_from_slice(&sha256(key));
    } else {
        key_block[..key.len()].copy_from_slice(key);
    }
    let mut ipad = [0x36u8; 64];
    let mut opad = [0x5cu8; 64];
    for index in 0..64 {
        ipad[index] ^= key_block[index];
        opad[index] ^= key_block[index];
    }
    let mut inner = Vec::with_capacity(64 + message.len());
    inner.extend_from_slice(&ipad);
    inner.extend_from_slice(message);
    let inner_hash = sha256(&inner);
    let mut outer = Vec::with_capacity(64 + inner_hash.len());
    outer.extend_from_slice(&opad);
    outer.extend_from_slice(&inner_hash);
    sha256(&outer)
}

fn sha256(message: &[u8]) -> [u8; 32] {
    const INITIAL: [u32; 8] = [
        0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab,
        0x5be0cd19,
    ];
    const K: [u32; 64] = [
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4,
        0xab1c5ed5, 0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe,
        0x9bdc06a7, 0xc19bf174, 0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f,
        0x4a7484aa, 0x5cb0a9dc, 0x76f988da, 0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7,
        0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967, 0x27b70a85, 0x2e1b2138, 0x4d2c6dfc,
        0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85, 0xa2bfe8a1, 0xa81a664b,
        0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070, 0x19a4c116,
        0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
        0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7,
        0xc67178f2,
    ];
    let bit_len = u64::try_from(message.len()).unwrap_or(u64::MAX / 8).saturating_mul(8);
    let padded_len = message.len().saturating_add(1).saturating_add(8).div_ceil(64) * 64;
    let mut padded = vec![0u8; padded_len];
    padded[..message.len()].copy_from_slice(message);
    padded[message.len()] = 0x80;
    padded[padded_len - 8..].copy_from_slice(&bit_len.to_be_bytes());

    let mut state = INITIAL;
    for chunk in padded.chunks_exact(64) {
        let mut schedule = [0u32; 64];
        for (index, word) in chunk.chunks_exact(4).take(16).enumerate() {
            schedule[index] = u32::from_be_bytes([word[0], word[1], word[2], word[3]]);
        }
        for index in 16..64 {
            let s0 = schedule[index - 15].rotate_right(7)
                ^ schedule[index - 15].rotate_right(18)
                ^ (schedule[index - 15] >> 3);
            let s1 = schedule[index - 2].rotate_right(17)
                ^ schedule[index - 2].rotate_right(19)
                ^ (schedule[index - 2] >> 10);
            schedule[index] = schedule[index - 16]
                .wrapping_add(s0)
                .wrapping_add(schedule[index - 7])
                .wrapping_add(s1);
        }
        let mut working = state;
        for index in 0..64 {
            let choose = (working[4] & working[5]) ^ (!working[4] & working[6]);
            let majority =
                (working[0] & working[1]) ^ (working[0] & working[2]) ^ (working[1] & working[2]);
            let s1 = working[4].rotate_right(6)
                ^ working[4].rotate_right(11)
                ^ working[4].rotate_right(25);
            let s0 = working[0].rotate_right(2)
                ^ working[0].rotate_right(13)
                ^ working[0].rotate_right(22);
            let temp1 = working[7]
                .wrapping_add(s1)
                .wrapping_add(choose)
                .wrapping_add(K[index])
                .wrapping_add(schedule[index]);
            let temp2 = s0.wrapping_add(majority);
            working[7] = working[6];
            working[6] = working[5];
            working[5] = working[4];
            working[4] = working[3].wrapping_add(temp1);
            working[3] = working[2];
            working[2] = working[1];
            working[1] = working[0];
            working[0] = temp1.wrapping_add(temp2);
        }
        for index in 0..8 {
            state[index] = state[index].wrapping_add(working[index]);
        }
    }
    let mut output = [0u8; 32];
    for (index, word) in state.iter().enumerate() {
        output[index * 4..index * 4 + 4].copy_from_slice(&word.to_be_bytes());
    }
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
    fn sha256_and_hmac_are_standard_vectors() {
        assert_eq!(
            hex_encode(&sha256(b"abc")),
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        );
        assert_eq!(
            hex_encode(&hmac_sha256(b"key", b"The quick brown fox jumps over the lazy dog")),
            "f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8"
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
}
