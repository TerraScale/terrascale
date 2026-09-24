//! Security primitives compatible with the persisted ts-auth C# formats.
// Result and Option already carry the obligation to handle failures and values.
// Repeating boilerplate for every small primitive obscures the protocol rules.
#![allow(clippy::missing_errors_doc, clippy::must_use_candidate)]

pub mod blake2b;
pub mod encoding;
pub mod jwt;
pub mod password;
pub mod pkce;
pub mod secretbox;
pub mod tokens;
pub mod totp;
pub mod webauthn;

pub use jwt::Es256Key;
