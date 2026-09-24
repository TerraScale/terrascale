//! `WebAuthn` registration and assertion verification for the persisted credential format.
use ciborium::value::Value;
use p256::ecdsa::{Signature as EcdsaSignature, VerifyingKey, signature::Verifier};
use rsa::{
    BigUint, RsaPublicKey,
    pkcs1v15::{Signature as RsaSignature, VerifyingKey as RsaVerifyingKey},
    pss::{Signature as PssSignature, VerifyingKey as PssVerifyingKey},
};
use serde_json::Value as JsonValue;
use sha2::{Digest, Sha256};
use std::io::Cursor;
use subtle::ConstantTimeEq;

#[derive(Debug, thiserror::Error)]
pub enum WebAuthnError {
    #[error("malformed WebAuthn data")]
    Format,
    #[error("unsupported authenticator key or attestation")]
    Unsupported,
    #[error("WebAuthn ceremony validation failed")]
    Validation,
    #[error("signature verification failed")]
    Signature,
    #[error("authenticator sign count did not advance")]
    SignCount,
}

#[derive(Clone)]
enum PublicKey {
    Ec(VerifyingKey),
    Rsa(RsaPublicKey),
}

#[derive(Clone)]
pub struct CoseKey {
    pub algorithm: &'static str,
    pub raw_cbor: Vec<u8>,
    public_key: PublicKey,
}

fn integer(value: &Value) -> Option<i64> {
    match value {
        Value::Integer(v) => (*v).try_into().ok(),
        _ => None,
    }
}
fn map_value(map: &[(Value, Value)], label: i64) -> Option<&Value> {
    map.iter()
        .find(|(key, _)| integer(key) == Some(label))
        .map(|(_, value)| value)
}
fn bytes(value: &Value) -> Option<&[u8]> {
    if let Value::Bytes(data) = value {
        Some(data)
    } else {
        None
    }
}

fn unique_map_keys(map: &[(Value, Value)]) -> bool {
    map.iter()
        .enumerate()
        .all(|(index, (key, _))| !map[..index].iter().any(|(previous, _)| previous == key))
}

impl CoseKey {
    /// Encodes a P-256 ES256 public key in the COSE EC2 format.
    pub fn encode_ec2(x: &[u8], y: &[u8]) -> Result<Vec<u8>, WebAuthnError> {
        if x.len() != 32 || y.len() != 32 {
            return Err(WebAuthnError::Format);
        }
        let mut point = [0_u8; 65];
        point[0] = 4;
        point[1..33].copy_from_slice(x);
        point[33..].copy_from_slice(y);
        VerifyingKey::from_sec1_bytes(&point).map_err(|_| WebAuthnError::Format)?;
        let value = Value::Map(vec![
            (Value::Integer(1.into()), Value::Integer(2.into())),
            (Value::Integer(3.into()), Value::Integer((-7).into())),
            (Value::Integer((-1).into()), Value::Integer(1.into())),
            (Value::Integer((-2).into()), Value::Bytes(x.to_vec())),
            (Value::Integer((-3).into()), Value::Bytes(y.to_vec())),
        ]);
        let mut raw = Vec::new();
        ciborium::into_writer(&value, &mut raw).map_err(|_| WebAuthnError::Format)?;
        Ok(raw)
    }

    pub fn export_ec2_coordinates(&self) -> Option<([u8; 32], [u8; 32])> {
        let PublicKey::Ec(key) = &self.public_key else {
            return None;
        };
        let point = key.to_encoded_point(false);
        let mut x = [0_u8; 32];
        let mut y = [0_u8; 32];
        x.copy_from_slice(point.x()?);
        y.copy_from_slice(point.y()?);
        Some((x, y))
    }

    /// Parse a `COSE_Key` from the start of a byte slice and retain its original CBOR bytes.
    pub fn parse(raw: &[u8]) -> Result<(Self, usize), WebAuthnError> {
        let mut cursor = Cursor::new(raw);
        let value: Value = ciborium::from_reader(&mut cursor).map_err(|_| WebAuthnError::Format)?;
        let consumed = usize::try_from(cursor.position()).map_err(|_| WebAuthnError::Format)?;
        let Value::Map(map) = value else {
            return Err(WebAuthnError::Format);
        };
        if !unique_map_keys(&map) {
            return Err(WebAuthnError::Format);
        }
        let kty = map_value(&map, 1)
            .and_then(integer)
            .ok_or(WebAuthnError::Format)?;
        let alg = map_value(&map, 3)
            .and_then(integer)
            .ok_or(WebAuthnError::Format)?;
        let (algorithm, public_key) = match (kty, alg) {
            (2, -7) if map_value(&map, -1).and_then(integer) == Some(1) => {
                let x = map_value(&map, -2)
                    .and_then(bytes)
                    .ok_or(WebAuthnError::Format)?;
                let y = map_value(&map, -3)
                    .and_then(bytes)
                    .ok_or(WebAuthnError::Format)?;
                if x.len() != 32 || y.len() != 32 {
                    return Err(WebAuthnError::Format);
                }
                let mut point = [0_u8; 65];
                point[0] = 4;
                point[1..33].copy_from_slice(x);
                point[33..].copy_from_slice(y);
                let key =
                    VerifyingKey::from_sec1_bytes(&point).map_err(|_| WebAuthnError::Format)?;
                ("ES256", PublicKey::Ec(key))
            }
            (3, -257 | -37) => {
                let n = map_value(&map, -1)
                    .and_then(bytes)
                    .ok_or(WebAuthnError::Format)?;
                let e = map_value(&map, -2)
                    .and_then(bytes)
                    .ok_or(WebAuthnError::Format)?;
                let key = RsaPublicKey::new(BigUint::from_bytes_be(n), BigUint::from_bytes_be(e))
                    .map_err(|_| WebAuthnError::Format)?;
                (
                    if alg == -257 { "RS256" } else { "PS256" },
                    PublicKey::Rsa(key),
                )
            }
            _ => return Err(WebAuthnError::Unsupported),
        };
        Ok((
            Self {
                algorithm,
                raw_cbor: raw[..consumed].to_vec(),
                public_key,
            },
            consumed,
        ))
    }

    pub fn from_stored(raw: &[u8]) -> Result<Self, WebAuthnError> {
        let (key, consumed) = Self::parse(raw)?;
        if consumed != raw.len() {
            return Err(WebAuthnError::Format);
        }
        Ok(key)
    }

    pub fn verify(&self, data: &[u8], signature: &[u8]) -> bool {
        match (&self.public_key, self.algorithm) {
            (PublicKey::Ec(key), "ES256") => EcdsaSignature::from_slice(signature)
                .or_else(|_| EcdsaSignature::from_der(signature))
                .is_ok_and(|signature| key.verify(data, &signature).is_ok()),
            (PublicKey::Rsa(key), "RS256") => {
                RsaSignature::try_from(signature).is_ok_and(|signature| {
                    RsaVerifyingKey::<Sha256>::new(key.clone())
                        .verify(data, &signature)
                        .is_ok()
                })
            }
            (PublicKey::Rsa(key), "PS256") => {
                PssSignature::try_from(signature).is_ok_and(|signature| {
                    PssVerifyingKey::<Sha256>::new(key.clone())
                        .verify(data, &signature)
                        .is_ok()
                })
            }
            _ => false,
        }
    }
}

pub struct AuthenticatorData {
    pub rp_id_hash: [u8; 32],
    pub user_present: bool,
    pub user_verified: bool,
    pub sign_count: u32,
    pub aaguid: Option<[u8; 16]>,
    pub credential_id: Option<Vec<u8>>,
    pub credential_public_key: Option<CoseKey>,
}

pub fn parse_authenticator_data(raw: &[u8]) -> Result<AuthenticatorData, WebAuthnError> {
    if raw.len() < 37 {
        return Err(WebAuthnError::Format);
    }
    let rp_id_hash = raw[..32].try_into().map_err(|_| WebAuthnError::Format)?;
    let flags = raw[32];
    let sign_count = u32::from_be_bytes(raw[33..37].try_into().map_err(|_| WebAuthnError::Format)?);
    let (aaguid, credential_id, credential_public_key) = if flags & 0x40 != 0 {
        if raw.len() < 55 {
            return Err(WebAuthnError::Format);
        }
        let id_len = usize::from(u16::from_be_bytes([raw[53], raw[54]]));
        if !(1..=1023).contains(&id_len) || raw.len() < 55 + id_len {
            return Err(WebAuthnError::Format);
        }
        let key = CoseKey::parse(&raw[55 + id_len..])?.0;
        (
            Some(raw[37..53].try_into().map_err(|_| WebAuthnError::Format)?),
            Some(raw[55..55 + id_len].to_vec()),
            Some(key),
        )
    } else {
        (None, None, None)
    };
    Ok(AuthenticatorData {
        rp_id_hash,
        user_present: flags & 1 != 0,
        user_verified: flags & 4 != 0,
        sign_count,
        aaguid,
        credential_id,
        credential_public_key,
    })
}

pub fn client_data_challenge(client_data_json: &str) -> Option<String> {
    let data: JsonValue = serde_json::from_str(client_data_json).ok()?;
    data.get("challenge")?.as_str().map(ToOwned::to_owned)
}

fn validate_client_data(
    raw: &str,
    ceremony_type: &str,
    challenge: &str,
    origin: &str,
) -> Result<(), WebAuthnError> {
    let data: JsonValue = serde_json::from_str(raw).map_err(|_| WebAuthnError::Format)?;
    let object = data.as_object().ok_or(WebAuthnError::Format)?;
    if object.get("type").and_then(JsonValue::as_str) != Some(ceremony_type)
        || object.get("challenge").and_then(JsonValue::as_str) != Some(challenge)
        || object
            .get("origin")
            .and_then(JsonValue::as_str)
            .is_none_or(|actual| {
                !actual
                    .trim_end_matches('/')
                    .eq_ignore_ascii_case(origin.trim_end_matches('/'))
            })
        || object.get("crossOrigin") == Some(&JsonValue::Bool(true))
    {
        return Err(WebAuthnError::Validation);
    }
    Ok(())
}

fn validate_authenticator(
    data: &AuthenticatorData,
    rp_id: &str,
    require_uv: bool,
) -> Result<(), WebAuthnError> {
    let expected = Sha256::digest(rp_id.as_bytes());
    if !bool::from(data.rp_id_hash.ct_eq(expected.as_ref()))
        || !data.user_present
        || (require_uv && !data.user_verified)
    {
        return Err(WebAuthnError::Validation);
    }
    Ok(())
}

fn signed_data(auth_data: &[u8], client_data_json: &str) -> Vec<u8> {
    let mut signed = Vec::with_capacity(auth_data.len() + 32);
    signed.extend_from_slice(auth_data);
    signed.extend_from_slice(&Sha256::digest(client_data_json.as_bytes()));
    signed
}

pub struct Registration {
    pub credential_id: Vec<u8>,
    pub public_key: CoseKey,
    pub sign_count: u32,
}

fn text_key<'a>(map: &'a [(Value, Value)], name: &str, numeric: i64) -> Option<&'a Value> {
    map.iter()
        .find(|(key, _)| {
            matches!(key, Value::Text(text) if text == name) || integer(key) == Some(numeric)
        })
        .map(|(_, value)| value)
}

fn statement_key<'a>(map: &'a [(Value, Value)], name: &str) -> Option<&'a Value> {
    map.iter()
        .find(|(key, _)| matches!(key, Value::Text(text) if text == name))
        .map(|(_, value)| value)
}

fn field_count(map: &[(Value, Value)], name: &str, numeric: i64) -> usize {
    map.iter()
        .filter(|(key, _)| {
            matches!(key, Value::Text(text) if text == name) || integer(key) == Some(numeric)
        })
        .count()
}

pub fn verify_registration(
    client_data_json: &str,
    attestation_object: &[u8],
    challenge: &str,
    rp_id: &str,
    origin: &str,
    require_uv: bool,
) -> Result<Registration, WebAuthnError> {
    validate_client_data(client_data_json, "webauthn.create", challenge, origin)?;
    let value: Value =
        ciborium::from_reader(attestation_object).map_err(|_| WebAuthnError::Format)?;
    let Value::Map(map) = value else {
        return Err(WebAuthnError::Format);
    };
    if !unique_map_keys(&map)
        || [("fmt", 1), ("attStmt", 2), ("authData", 3)]
            .iter()
            .any(|(name, numeric)| field_count(&map, name, *numeric) != 1)
    {
        return Err(WebAuthnError::Format);
    }
    let fmt = match text_key(&map, "fmt", 1) {
        Some(Value::Text(text)) => text.as_str(),
        _ => return Err(WebAuthnError::Format),
    };
    let Some(Value::Map(statement)) = text_key(&map, "attStmt", 2) else {
        return Err(WebAuthnError::Format);
    };
    if !unique_map_keys(statement) {
        return Err(WebAuthnError::Format);
    }
    let auth_bytes = text_key(&map, "authData", 3)
        .and_then(bytes)
        .ok_or(WebAuthnError::Format)?;
    let data = parse_authenticator_data(auth_bytes)?;
    validate_authenticator(&data, rp_id, require_uv)?;
    let credential_id = data.credential_id.ok_or(WebAuthnError::Validation)?;
    let public_key = data
        .credential_public_key
        .ok_or(WebAuthnError::Validation)?;
    match fmt {
        "none" if statement.is_empty() => {}
        "packed" => {
            let algorithm = statement_key(statement, "alg")
                .and_then(integer)
                .ok_or(WebAuthnError::Format)?;
            let signature = statement_key(statement, "sig")
                .and_then(bytes)
                .ok_or(WebAuthnError::Format)?;
            let signed = signed_data(auth_bytes, client_data_json);
            if let Some(Value::Array(certificates)) = statement_key(statement, "x5c") {
                let certificate = certificates
                    .first()
                    .and_then(bytes)
                    .ok_or(WebAuthnError::Format)?;
                let (_, cert) = x509_parser::parse_x509_certificate(certificate)
                    .map_err(|_| WebAuthnError::Format)?;
                let spki = cert.public_key();
                let raw_key = spki.subject_public_key.data.as_ref();
                let verified = match algorithm {
                    -7 => ring::signature::UnparsedPublicKey::new(
                        &ring::signature::ECDSA_P256_SHA256_ASN1,
                        raw_key,
                    )
                    .verify(&signed, signature)
                    .is_ok(),
                    -257 => ring::signature::UnparsedPublicKey::new(
                        &ring::signature::RSA_PKCS1_2048_8192_SHA256,
                        raw_key,
                    )
                    .verify(&signed, signature)
                    .is_ok(),
                    -37 => ring::signature::UnparsedPublicKey::new(
                        &ring::signature::RSA_PSS_2048_8192_SHA256,
                        raw_key,
                    )
                    .verify(&signed, signature)
                    .is_ok(),
                    _ => return Err(WebAuthnError::Unsupported),
                };
                if !verified {
                    return Err(WebAuthnError::Signature);
                }
            } else {
                let expected = match public_key.algorithm {
                    "ES256" => -7,
                    "RS256" => -257,
                    "PS256" => -37,
                    _ => return Err(WebAuthnError::Unsupported),
                };
                if algorithm != expected {
                    return Err(WebAuthnError::Validation);
                }
                if !public_key.verify(&signed, signature) {
                    return Err(WebAuthnError::Signature);
                }
            }
        }
        _ => return Err(WebAuthnError::Unsupported),
    }
    Ok(Registration {
        credential_id,
        public_key,
        sign_count: data.sign_count,
    })
}

pub struct Assertion {
    pub sign_count: u32,
    pub user_verified: bool,
}

#[expect(
    clippy::too_many_arguments,
    reason = "WebAuthn assertion inputs are supplied by distinct protocol sources"
)]
pub fn verify_assertion(
    client_data_json: &str,
    authenticator_data: &[u8],
    signature: &[u8],
    challenge: &str,
    rp_id: &str,
    origin: &str,
    credential_id: &[u8],
    public_key: &CoseKey,
    stored_sign_count: u32,
    require_uv: bool,
) -> Result<Assertion, WebAuthnError> {
    validate_client_data(client_data_json, "webauthn.get", challenge, origin)?;
    let data = parse_authenticator_data(authenticator_data)?;
    validate_authenticator(&data, rp_id, require_uv)?;
    if data
        .credential_id
        .as_deref()
        .is_some_and(|id| id != credential_id)
    {
        return Err(WebAuthnError::Validation);
    }
    if !public_key.verify(
        &signed_data(authenticator_data, client_data_json),
        signature,
    ) {
        return Err(WebAuthnError::Signature);
    }
    if (data.sign_count != 0 || stored_sign_count != 0) && data.sign_count <= stored_sign_count {
        return Err(WebAuthnError::SignCount);
    }
    Ok(Assertion {
        sign_count: data.sign_count,
        user_verified: data.user_verified,
    })
}
