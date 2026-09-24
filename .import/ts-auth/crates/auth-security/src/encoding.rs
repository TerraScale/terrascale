use base64::{
    Engine,
    engine::general_purpose::{STANDARD, URL_SAFE_NO_PAD},
};

pub fn base64url_encode(data: &[u8]) -> String {
    URL_SAFE_NO_PAD.encode(data)
}

pub fn base64url_decode(text: &str) -> Result<Vec<u8>, base64::DecodeError> {
    // C# accepted both padded and unpadded base64url (and the standard alphabet).
    let normalized = text
        .trim_end_matches('=')
        .replace('-', "+")
        .replace('_', "/");
    let padding = "=".repeat((4 - normalized.len() % 4) % 4);
    STANDARD.decode(format!("{normalized}{padding}"))
}

pub fn base32_encode(data: &[u8]) -> String {
    data_encoding::BASE32_NOPAD.encode(data)
}

pub fn base32_decode(text: &str) -> Result<Vec<u8>, data_encoding::DecodeError> {
    let normalized: String = text
        .chars()
        .filter(|c| !matches!(c, '=' | ' ' | '\r' | '\n'))
        .map(|c| c.to_ascii_uppercase())
        .collect();
    data_encoding::BASE32_NOPAD.decode(normalized.as_bytes())
}

pub fn hex_encode(data: &[u8]) -> String {
    hex::encode(data)
}
pub fn hex_decode(text: &str) -> Result<Vec<u8>, hex::FromHexError> {
    hex::decode(text)
}
