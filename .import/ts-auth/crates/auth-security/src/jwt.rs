use base64::{Engine, engine::general_purpose::URL_SAFE_NO_PAD};
use hmac::{Hmac, Mac};
use p256::ecdsa::{
    Signature as EcdsaSignature, SigningKey, VerifyingKey,
    signature::{Signer, Verifier},
};
use rand::rngs::OsRng;
use rsa::{
    BigUint, RsaPublicKey,
    pkcs1v15::{Signature as RsaSignature, VerifyingKey as RsaVerifyingKey},
};
use serde_json::{Value, json};
use sha2::Sha256;
use subtle::ConstantTimeEq;

#[derive(Debug, thiserror::Error)]
pub enum JwtError {
    #[error("invalid key material")]
    Key,
    #[error("claims must be a JSON object")]
    Claims,
    #[error("JSON serialization failed")]
    Json(#[from] serde_json::Error),
}

pub trait JwtKey {
    fn kid(&self) -> &str;
    fn algorithm(&self) -> &'static str;
    fn sign(&self, input: &[u8]) -> Option<Vec<u8>>;
    fn verify(&self, input: &[u8], signature: &[u8]) -> bool;
    fn public_jwk(&self) -> Value;
}

pub struct Es256Key {
    kid: String,
    key: SigningKey,
}

impl Es256Key {
    pub fn generate(kid: impl Into<String>) -> Self {
        Self {
            kid: kid.into(),
            key: SigningKey::random(&mut OsRng),
        }
    }

    /// Persisted C# format is d || X || Y, each a 32-byte big-endian integer.
    pub fn from_secret(kid: impl Into<String>, secret: &[u8]) -> Result<Self, JwtError> {
        if secret.len() != 96 {
            return Err(JwtError::Key);
        }
        let key = SigningKey::from_bytes(secret[..32].into()).map_err(|_| JwtError::Key)?;
        let point = key.verifying_key().to_encoded_point(false);
        if point.as_bytes()[1..] != secret[32..] {
            return Err(JwtError::Key);
        }
        Ok(Self {
            kid: kid.into(),
            key,
        })
    }

    pub fn export_secret(&self) -> [u8; 96] {
        let mut secret = [0_u8; 96];
        secret[..32].copy_from_slice(self.key.to_bytes().as_ref());
        secret[32..]
            .copy_from_slice(&self.key.verifying_key().to_encoded_point(false).as_bytes()[1..]);
        secret
    }

    pub fn kid(&self) -> &str {
        &self.kid
    }
    pub fn sign(&self, input: &[u8]) -> [u8; 64] {
        let signature: EcdsaSignature = self.key.sign(input);
        signature.to_bytes().into()
    }
    pub fn verify(&self, input: &[u8], signature: &[u8]) -> bool {
        let Ok(signature) = EcdsaSignature::from_slice(signature) else {
            return false;
        };
        self.key.verifying_key().verify(input, &signature).is_ok()
    }
    /// # Panics
    ///
    /// This can only panic if the P-256 library returns a compressed point
    /// after an uncompressed point was requested.
    pub fn public_jwk(&self) -> Value {
        let point = self.key.verifying_key().to_encoded_point(false);
        json!({"kty":"EC","crv":"P-256","use":"sig","alg":"ES256","kid":self.kid,
            "x": URL_SAFE_NO_PAD.encode(point.x().expect("uncompressed point")),
            "y": URL_SAFE_NO_PAD.encode(point.y().expect("uncompressed point"))})
    }
}

impl JwtKey for Es256Key {
    fn kid(&self) -> &str {
        self.kid()
    }
    fn algorithm(&self) -> &'static str {
        "ES256"
    }
    fn sign(&self, input: &[u8]) -> Option<Vec<u8>> {
        Some(self.sign(input).to_vec())
    }
    fn verify(&self, input: &[u8], signature: &[u8]) -> bool {
        self.verify(input, signature)
    }
    fn public_jwk(&self) -> Value {
        self.public_jwk()
    }
}

pub struct Hs256Key {
    kid: String,
    secret: Vec<u8>,
}

pub struct Es256PublicKey {
    kid: String,
    key: VerifyingKey,
}

impl Es256PublicKey {
    pub fn from_coordinates(kid: impl Into<String>, x: &[u8], y: &[u8]) -> Result<Self, JwtError> {
        if x.len() != 32 || y.len() != 32 {
            return Err(JwtError::Key);
        }
        let mut point = [0_u8; 65];
        point[0] = 4;
        point[1..33].copy_from_slice(x);
        point[33..].copy_from_slice(y);
        let key = VerifyingKey::from_sec1_bytes(&point).map_err(|_| JwtError::Key)?;
        Ok(Self {
            kid: kid.into(),
            key,
        })
    }
}

impl JwtKey for Es256PublicKey {
    fn kid(&self) -> &str {
        &self.kid
    }
    fn algorithm(&self) -> &'static str {
        "ES256"
    }
    fn sign(&self, _: &[u8]) -> Option<Vec<u8>> {
        None
    }
    fn verify(&self, input: &[u8], signature: &[u8]) -> bool {
        let Ok(signature) = EcdsaSignature::from_slice(signature) else {
            return false;
        };
        self.key.verify(input, &signature).is_ok()
    }
    fn public_jwk(&self) -> Value {
        let point = self.key.to_encoded_point(false);
        json!({"kty":"EC","crv":"P-256","use":"sig","alg":"ES256","kid":self.kid,
            "x": URL_SAFE_NO_PAD.encode(point.x().expect("uncompressed point")),
            "y": URL_SAFE_NO_PAD.encode(point.y().expect("uncompressed point"))})
    }
}
impl Hs256Key {
    pub fn new(kid: impl Into<String>, secret: Vec<u8>) -> Self {
        Self {
            kid: kid.into(),
            secret,
        }
    }
}
impl JwtKey for Hs256Key {
    fn kid(&self) -> &str {
        &self.kid
    }
    fn algorithm(&self) -> &'static str {
        "HS256"
    }
    fn sign(&self, input: &[u8]) -> Option<Vec<u8>> {
        let mut mac = Hmac::<Sha256>::new_from_slice(&self.secret).ok()?;
        mac.update(input);
        Some(mac.finalize().into_bytes().to_vec())
    }
    fn verify(&self, input: &[u8], signature: &[u8]) -> bool {
        self.sign(input)
            .is_some_and(|expected| bool::from(expected.ct_eq(signature)))
    }
    fn public_jwk(&self) -> Value {
        json!({"kty":"oct","use":"sig","alg":"HS256","kid":self.kid})
    }
}

pub struct Rs256PublicKey {
    kid: String,
    key: RsaPublicKey,
}
impl Rs256PublicKey {
    pub fn from_modulus_exponent(
        kid: impl Into<String>,
        modulus: &[u8],
        exponent: &[u8],
    ) -> Result<Self, JwtError> {
        let key = RsaPublicKey::new(
            BigUint::from_bytes_be(modulus),
            BigUint::from_bytes_be(exponent),
        )
        .map_err(|_| JwtError::Key)?;
        Ok(Self {
            kid: kid.into(),
            key,
        })
    }
}
impl JwtKey for Rs256PublicKey {
    fn kid(&self) -> &str {
        &self.kid
    }
    fn algorithm(&self) -> &'static str {
        "RS256"
    }
    fn sign(&self, _: &[u8]) -> Option<Vec<u8>> {
        None
    }
    fn verify(&self, input: &[u8], signature: &[u8]) -> bool {
        let Ok(signature) = RsaSignature::try_from(signature) else {
            return false;
        };
        RsaVerifyingKey::<Sha256>::new(self.key.clone())
            .verify(input, &signature)
            .is_ok()
    }
    fn public_jwk(&self) -> Value {
        use rsa::traits::PublicKeyParts;
        json!({"kty":"RSA","use":"sig","alg":"RS256","kid":self.kid,
            "n": URL_SAFE_NO_PAD.encode(self.key.n().to_bytes_be()),
            "e": URL_SAFE_NO_PAD.encode(self.key.e().to_bytes_be())})
    }
}

/// Builds a verification-only key from an untrusted JWK. Symmetric and private
/// key material are never imported from a remote JWKS. The optional JWK `alg`
/// must agree with the key type when present; the JWT header is checked later.
pub fn public_key_from_jwk(jwk: &Value) -> Result<Box<dyn JwtKey>, JwtError> {
    let object = jwk.as_object().ok_or(JwtError::Key)?;
    let kid = match object.get("kid") {
        None => "",
        Some(Value::String(kid)) if !kid.is_empty() => kid.as_str(),
        _ => return Err(JwtError::Key),
    };
    if object
        .get("use")
        .is_some_and(|value| value.as_str() != Some("sig"))
    {
        return Err(JwtError::Key);
    }
    if let Some(operations) = object.get("key_ops") {
        let operations = operations.as_array().ok_or(JwtError::Key)?;
        if !operations
            .iter()
            .any(|operation| operation.as_str() == Some("verify"))
        {
            return Err(JwtError::Key);
        }
    }
    let decode = |field: &str| -> Result<Vec<u8>, JwtError> {
        let value = object
            .get(field)
            .and_then(Value::as_str)
            .ok_or(JwtError::Key)?;
        URL_SAFE_NO_PAD.decode(value).map_err(|_| JwtError::Key)
    };
    match (
        object.get("kty").and_then(Value::as_str),
        object.get("alg").map(Value::as_str),
    ) {
        (Some("EC"), None | Some(Some("ES256")))
            if object.get("crv").and_then(Value::as_str) == Some("P-256") =>
        {
            if object.contains_key("d") {
                return Err(JwtError::Key);
            }
            Ok(Box::new(Es256PublicKey::from_coordinates(
                kid,
                &decode("x")?,
                &decode("y")?,
            )?))
        }
        (Some("RSA"), None | Some(Some("RS256"))) => {
            if ["d", "p", "q", "dp", "dq", "qi", "oth"]
                .iter()
                .any(|field| object.contains_key(*field))
            {
                return Err(JwtError::Key);
            }
            let modulus = decode("n")?;
            let bits = BigUint::from_bytes_be(&modulus).bits();
            if !(2048..=8192).contains(&bits) {
                return Err(JwtError::Key);
            }
            Ok(Box::new(Rs256PublicKey::from_modulus_exponent(
                kid,
                &modulus,
                &decode("e")?,
            )?))
        }
        _ => Err(JwtError::Key),
    }
}

pub fn sign(
    mut claims: Value,
    expires: Option<i64>,
    key: &impl JwtKey,
    now: i64,
) -> Result<String, JwtError> {
    let payload = claims.as_object_mut().ok_or(JwtError::Claims)?;
    if let Some(exp) = expires {
        payload.insert("exp".into(), json!(exp));
    }
    payload.entry("iat").or_insert_with(|| json!(now));
    let header = json!({"alg":key.algorithm(),"typ":"JWT","kid":key.kid()});
    let header = URL_SAFE_NO_PAD.encode(serde_json::to_vec(&header)?);
    let payload = URL_SAFE_NO_PAD.encode(serde_json::to_vec(&claims)?);
    let signed = format!("{header}.{payload}");
    let signature = key.sign(signed.as_bytes()).ok_or(JwtError::Key)?;
    Ok(format!("{signed}.{}", URL_SAFE_NO_PAD.encode(signature)))
}

/// Signature and `exp` verification only. Callers must check issuer and audience.
pub fn verify(
    token: &str,
    resolve_key: impl FnOnce(&str) -> Option<Box<dyn JwtKey>>,
    now: i64,
) -> Option<Value> {
    let header = decode_header(token)?;
    let kid = header.get("kid")?.as_str()?;
    if kid.is_empty() {
        return None;
    }
    let key = resolve_key(kid)?;
    verify_with_key(token, key.as_ref(), now, false)
}

/// Verifies a JWT against a set of public keys. A token without `kid` is
/// accepted only when exactly one key supports the declared algorithm. This
/// allows single-key OIDC JWKS while rejecting ambiguous key selection.
/// Callers must also check issuer, audience, and any nonce requirements.
pub fn verify_with_keys(token: &str, keys: &[Box<dyn JwtKey>], now: i64) -> Option<Value> {
    let header = decode_header(token)?;
    let algorithm = header.get("alg")?.as_str()?;
    if !matches!(algorithm, "ES256" | "RS256") {
        return None;
    }
    let kid = match header.get("kid") {
        Some(Value::String(kid)) if !kid.is_empty() => Some(kid.as_str()),
        Some(_) => return None,
        None => None,
    };
    let mut matches = keys
        .iter()
        .filter(|key| key.algorithm() == algorithm && kid.is_none_or(|id| key.kid() == id));
    let key = matches.next()?;
    if matches.next().is_some() {
        return None;
    }
    verify_with_key(token, key.as_ref(), now, kid.is_none())
}

fn decode_header(token: &str) -> Option<Value> {
    let header = token.split('.').next()?;
    serde_json::from_slice(&URL_SAFE_NO_PAD.decode(header).ok()?).ok()
}

fn verify_with_key(
    token: &str,
    key: &dyn JwtKey,
    now: i64,
    allow_missing_kid: bool,
) -> Option<Value> {
    let mut parts = token.split('.');
    let (Some(header), Some(payload), Some(signature), None) =
        (parts.next(), parts.next(), parts.next(), parts.next())
    else {
        return None;
    };
    let header_json: Value = serde_json::from_slice(&URL_SAFE_NO_PAD.decode(header).ok()?).ok()?;
    let payload_json: Value =
        serde_json::from_slice(&URL_SAFE_NO_PAD.decode(payload).ok()?).ok()?;
    payload_json.as_object()?;
    let signature = URL_SAFE_NO_PAD.decode(signature).ok()?;
    let algorithm = header_json.get("alg")?.as_str()?;
    let kid_matches = match header_json.get("kid") {
        Some(Value::String(kid)) if !kid.is_empty() => key.kid() == kid,
        None => allow_missing_kid,
        _ => false,
    };
    if !kid_matches
        || key.algorithm() != algorithm
        || !key.verify(format!("{header}.{payload}").as_bytes(), &signature)
    {
        return None;
    }
    if let Some(expires) = payload_json.get("exp") {
        if now >= expires.as_i64()? {
            return None;
        }
    }
    Some(payload_json)
}

pub fn decode_unverified(token: &str) -> Option<Value> {
    let mut parts = token.split('.');
    parts.next()?;
    let payload: Value =
        serde_json::from_slice(&URL_SAFE_NO_PAD.decode(parts.next()?).ok()?).ok()?;
    parts.next()?;
    if parts.next().is_some() {
        return None;
    }
    payload.as_object()?;
    Some(payload)
}
