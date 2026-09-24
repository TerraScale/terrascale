//! WebAuthn verification lives in `auth_security::webauthn`; this module holds app adapters.
pub use auth_security::webauthn::{
    Assertion, CoseKey, Registration, WebAuthnError, client_data_challenge, verify_assertion,
    verify_registration,
};
