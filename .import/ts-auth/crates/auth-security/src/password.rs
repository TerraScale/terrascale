use argon2::{Algorithm, Argon2, AssociatedData, Params, ParamsBuilder, Version};
use base64::{Engine, engine::general_purpose::STANDARD_NO_PAD};
use rand::{RngCore, rngs::OsRng};
use subtle::ConstantTimeEq;

pub const DEFAULT_MEMORY_KIB: u32 = 19_456;
pub const DEFAULT_PASSES: u32 = 2;
pub const DEFAULT_LANES: u32 = 1;

#[derive(Debug, thiserror::Error)]
pub enum PasswordError {
    #[error("invalid Argon2 parameters")]
    InvalidParameters,
    #[error("password derivation failed")]
    Derivation,
}

#[derive(Debug, Clone, Copy)]
pub struct Argon2idParams {
    pub memory_kib: u32,
    pub passes: u32,
    pub lanes: u32,
    pub tag_size: usize,
}

/// RFC 9106 Argon2id v1.3 derivation, including optional secret and associated
/// data inputs. Callers must bound untrusted work factors before invoking it.
pub fn derive_argon2id(
    password: &[u8],
    salt: &[u8],
    settings: Argon2idParams,
    secret: &[u8],
    associated_data: &[u8],
) -> Result<Vec<u8>, PasswordError> {
    let data =
        AssociatedData::new(associated_data).map_err(|_| PasswordError::InvalidParameters)?;
    let params = ParamsBuilder::new()
        .m_cost(settings.memory_kib)
        .t_cost(settings.passes)
        .p_cost(settings.lanes)
        .output_len(settings.tag_size)
        .data(data)
        .build()
        .map_err(|_| PasswordError::InvalidParameters)?;
    let argon = Argon2::new_with_secret(secret, Algorithm::Argon2id, Version::V0x13, params)
        .map_err(|_| PasswordError::InvalidParameters)?;
    let mut tag = vec![0; settings.tag_size];
    argon
        .hash_password_into(password, salt, &mut tag)
        .map_err(|_| PasswordError::Derivation)?;
    Ok(tag)
}

pub fn hash(password: &str) -> Result<String, PasswordError> {
    hash_with_params(password, DEFAULT_MEMORY_KIB, DEFAULT_PASSES, DEFAULT_LANES)
}

pub fn hash_with_params(
    password: &str,
    memory_kib: u32,
    passes: u32,
    lanes: u32,
) -> Result<String, PasswordError> {
    let params = Params::new(memory_kib, passes, lanes, Some(32))
        .map_err(|_| PasswordError::InvalidParameters)?;
    let mut salt = [0_u8; 16];
    OsRng.fill_bytes(&mut salt);
    let mut tag = [0_u8; 32];
    Argon2::new(Algorithm::Argon2id, Version::V0x13, params)
        .hash_password_into(password.as_bytes(), &salt, &mut tag)
        .map_err(|_| PasswordError::Derivation)?;
    Ok(format!(
        "$argon2id$v=19$m={memory_kib},t={passes},p={lanes}${}${}",
        STANDARD_NO_PAD.encode(salt),
        STANDARD_NO_PAD.encode(tag)
    ))
}

/// Returns false for malformed hashes. Resource limits are imposed before derivation.
pub fn verify(password: &str, phc: &str) -> bool {
    let Some(rest) = phc.strip_prefix("$argon2id$v=19$") else {
        return false;
    };
    let mut parts = rest.split('$');
    let (Some(params), Some(salt_text), Some(tag_text), None) =
        (parts.next(), parts.next(), parts.next(), parts.next())
    else {
        return false;
    };
    let mut memory = None;
    let mut passes = None;
    let mut lanes = None;
    for pair in params.split(',') {
        let Some((name, value)) = pair.split_once('=') else {
            return false;
        };
        let Ok(value) = value.parse::<u32>() else {
            return false;
        };
        match name {
            "m" => memory = Some(value),
            "t" => passes = Some(value),
            "p" => lanes = Some(value),
            _ => return false,
        }
    }
    let (Some(memory), Some(passes), Some(lanes)) = (memory, passes, lanes) else {
        return false;
    };
    // Malformed untrusted PHC parameters must not exhaust the service.
    if memory > 1_048_576 || passes > 32 || lanes > 16 {
        return false;
    }
    let Ok(salt) = STANDARD_NO_PAD.decode(salt_text) else {
        return false;
    };
    let Ok(expected) = STANDARD_NO_PAD.decode(tag_text) else {
        return false;
    };
    if expected.len() < 4 || expected.len() > 1024 {
        return false;
    }
    let Ok(params) = Params::new(memory, passes, lanes, Some(expected.len())) else {
        return false;
    };
    let mut actual = vec![0; expected.len()];
    if Argon2::new(Algorithm::Argon2id, Version::V0x13, params)
        .hash_password_into(password.as_bytes(), &salt, &mut actual)
        .is_err()
    {
        return false;
    }
    actual.ct_eq(&expected).into()
}
