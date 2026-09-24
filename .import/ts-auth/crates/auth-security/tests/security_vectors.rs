use auth_security::{blake2b, encoding, jwt, password, pkce, secretbox, tokens, totp, webauthn};
use serde_json::json;

#[test]
fn totp_matches_rfc_6238_and_one_step_window() {
    let seed = b"12345678901234567890";
    assert_eq!(
        totp::hotp(seed, 1, 8, totp::Algorithm::Sha1).as_deref(),
        Some("94287082")
    );
    assert_eq!(
        totp::hotp(
            b"12345678901234567890123456789012",
            1,
            8,
            totp::Algorithm::Sha256
        )
        .as_deref(),
        Some("46119246")
    );
    assert_eq!(
        totp::hotp(
            b"1234567890123456789012345678901234567890123456789012345678901234",
            1,
            8,
            totp::Algorithm::Sha512
        )
        .as_deref(),
        Some("90693936")
    );
    let code = totp::compute(seed, 59).unwrap();
    assert!(totp::verify(seed, &code, 89));
    assert!(!totp::verify(seed, &code, 179));
}

#[test]
fn password_phc_roundtrip_and_malformed_rejection() {
    let phc = password::hash_with_params("correct horse battery staple", 32, 1, 1).unwrap();
    assert!(phc.starts_with("$argon2id$v=19$m=32,t=1,p=1$"));
    assert!(password::verify("correct horse battery staple", &phc));
    assert!(!password::verify("wrong", &phc));
    assert!(!password::verify(
        "pw",
        "$argon2id$v=19$m=999999999,t=1,p=1$c2FsdA$aGFzaA"
    ));
    assert!(!password::verify(
        "pw",
        "$argon2id$v=19$m=32,t=1,p=1$c2FsdA$AA"
    ));
}

#[test]
fn secretbox_uses_v1_nonce_ciphertext_tag_and_authenticates() {
    let key = [7_u8; 32];
    let boxed = secretbox::encrypt(&key, b"totp seed").unwrap();
    assert!(boxed.starts_with("v1:"));
    assert_eq!(secretbox::decrypt(&key, &boxed).unwrap(), b"totp seed");
    let mut raw =
        base64::Engine::decode(&base64::engine::general_purpose::STANDARD, &boxed[3..]).unwrap();
    assert_eq!(raw.len(), 12 + 9 + 16);
    *raw.last_mut().unwrap() ^= 1;
    let tampered = format!(
        "v1:{}",
        base64::Engine::encode(&base64::engine::general_purpose::STANDARD, raw)
    );
    assert!(secretbox::decrypt(&key, &tampered).is_err());
}

#[test]
fn es256_persisted_secret_and_algorithm_binding() {
    let key = jwt::Es256Key::generate("kid-1");
    let restored = jwt::Es256Key::from_secret("kid-1", &key.export_secret()).unwrap();
    let token = jwt::sign(json!({"sub":"user_1"}), Some(100), &key, 1).unwrap();
    assert_eq!(
        jwt::verify(&token, |_| Some(Box::new(restored)), 2).unwrap()["sub"],
        "user_1"
    );
    let restored = jwt::Es256Key::from_secret("kid-1", &key.export_secret()).unwrap();
    assert!(jwt::verify(&token, |_| Some(Box::new(restored)), 100).is_none());
    let malformed_exp = jwt::sign(json!({"exp":"0"}), None, &key, 1).unwrap();
    let restored = jwt::Es256Key::from_secret("kid-1", &key.export_secret()).unwrap();
    assert!(jwt::verify(&malformed_exp, |_| Some(Box::new(restored)), 2).is_none());
    let mut secret = key.export_secret();
    secret[80] ^= 1;
    assert!(jwt::Es256Key::from_secret("kid-1", &secret).is_err());
    assert!(key.public_jwk().get("d").is_none());
    assert_eq!(key.sign(b"input").len(), 64);
}

#[test]
fn jwk_import_is_verification_only_and_binds_kid() {
    let key = jwt::Es256Key::generate("expected");
    let public = key.public_jwk();
    let imported = jwt::public_key_from_jwk(&public).unwrap();
    let token = jwt::sign(json!({"sub":"alice"}), Some(100), &key, 1).unwrap();
    assert_eq!(
        jwt::verify(&token, |_| Some(imported), 2).unwrap()["sub"],
        "alice"
    );
    let wrong_kid = jwt::Es256PublicKey::from_coordinates(
        "different",
        &encoding::base64url_decode(public["x"].as_str().unwrap()).unwrap(),
        &encoding::base64url_decode(public["y"].as_str().unwrap()).unwrap(),
    )
    .unwrap();
    assert!(jwt::verify(&token, |_| Some(Box::new(wrong_kid)), 2).is_none());
    let mut private = public.clone();
    private["d"] = json!("should-not-be-accepted");
    assert!(jwt::public_key_from_jwk(&private).is_err());
    let mut unspecified_algorithm = public.clone();
    unspecified_algorithm.as_object_mut().unwrap().remove("alg");
    assert!(jwt::public_key_from_jwk(&unspecified_algorithm).is_ok());
    let mut weak = public;
    weak["alg"] = json!("none");
    assert!(jwt::public_key_from_jwk(&weak).is_err());
    assert!(jwt::decode_unverified("abc.def.ghi.extra").is_none());
}

#[test]
fn kidless_jwt_requires_unique_matching_public_key() {
    let signing_key = jwt::Es256Key::generate("source-kid");
    let mut jwk = signing_key.public_jwk();
    jwk.as_object_mut().unwrap().remove("kid");
    let key = jwt::public_key_from_jwk(&jwk).unwrap();
    let header = encoding::base64url_encode(br#"{"alg":"ES256","typ":"JWT"}"#);
    let payload = encoding::base64url_encode(br#"{"sub":"alice","exp":100}"#);
    let input = format!("{header}.{payload}");
    let token = format!(
        "{input}.{}",
        encoding::base64url_encode(&signing_key.sign(input.as_bytes()))
    );
    assert!(jwt::verify(&token, |_| Some(jwt::public_key_from_jwk(&jwk).unwrap()), 1).is_none());
    assert_eq!(
        jwt::verify_with_keys(&token, &[key], 1).unwrap()["sub"],
        "alice"
    );
    let second = jwt::public_key_from_jwk(&jwk).unwrap();
    let third = jwt::public_key_from_jwk(&jwk).unwrap();
    assert!(jwt::verify_with_keys(&token, &[second, third], 1).is_none());
    let wrong_algorithm: Box<dyn jwt::JwtKey> = Box::new(jwt::Hs256Key::new("hmac", vec![3; 32]));
    assert!(jwt::verify_with_keys(&token, &[wrong_algorithm], 1).is_none());
}

#[test]
fn encoding_and_token_formats_match_csharp() {
    assert_eq!(encoding::base32_encode(b"foob"), "MZXW6YQ");
    assert_eq!(encoding::base32_decode("mzxw6yq=").unwrap(), b"foob");
    assert_eq!(
        tokens::sha256_hex("abc"),
        "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
    );
    let (id, secret, combined) = tokens::new_secret("sk");
    assert_eq!(id.len(), 25);
    assert_eq!(secret.len(), 40);
    assert_eq!(combined, format!("{id}_{secret}"));
    assert_eq!(
        webauthn::client_data_challenge(r#"{"challenge":"abc"}"#).as_deref(),
        Some("abc")
    );
}

#[test]
fn blake2b_known_vectors_and_streaming() {
    assert_eq!(
        hex::encode(blake2b::hash(64, b"").unwrap()),
        "786a02f742015903c6c6fd852552d272912f4740e15847618a86e217f71f5419d25e1031afee585313896444934eb04b903a685b1448b755d56f701afe9be2ce"
    );
    assert_eq!(
        hex::encode(blake2b::hash(64, b"abc").unwrap()),
        "ba80a53f981c4d0d6a2797b69f12f6e94c212f14685ac4b74b12bb6fdbffa2d17d87c5392aab792dc252d5de4533cc9518d38aa8dbf1925ab92386edd4009923"
    );
    let mut stream = blake2b::Blake2b::new(32).unwrap();
    for chunk in b"streamed input".chunks(3) {
        stream.update(chunk);
    }
    assert_eq!(
        stream.finalize(),
        blake2b::hash(32, b"streamed input").unwrap()
    );
    assert!(blake2b::hash(0, b"").is_err());
    assert!(blake2b::hash(65, b"").is_err());
}

#[test]
fn argon2id_rfc_9106_secret_and_associated_data_vector() {
    let tag = password::derive_argon2id(
        &[1; 32],
        &[2; 16],
        password::Argon2idParams {
            memory_kib: 32,
            passes: 3,
            lanes: 4,
            tag_size: 32,
        },
        &[3; 8],
        &[4; 12],
    )
    .unwrap();
    assert_eq!(
        hex::encode(tag),
        "0d640df58d78766c08c037a34a8b53c9d01ef0452d75b65eb52520e96b01e659"
    );
}

#[test]
fn pkce_rfc_7636_vector_and_verifier_grammar() {
    let verifier = "dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk";
    let challenge = "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM";
    assert_eq!(pkce::challenge_s256(verifier).as_deref(), Some(challenge));
    assert!(pkce::verify_s256(verifier, challenge));
    assert!(!pkce::verify_s256("too-short", challenge));
    assert!(!pkce::verify_s256(
        verifier,
        "E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-dM"
    ));
}

#[test]
#[expect(
    clippy::too_many_lines,
    reason = "the test builds one complete WebAuthn ceremony fixture"
)]
fn webauthn_packed_self_attestation_requires_matching_algorithm_and_valid_signature() {
    use ciborium::value::Value as Cbor;
    use p256::ecdsa::{Signature, SigningKey, signature::Signer};
    use sha2::{Digest, Sha256};
    let signing_key = SigningKey::random(&mut rand::rngs::OsRng);
    let point = signing_key.verifying_key().to_encoded_point(false);
    let cose = webauthn::CoseKey::encode_ec2(point.x().unwrap(), point.y().unwrap()).unwrap();
    let key = webauthn::CoseKey::from_stored(&cose).unwrap();
    let (x, y) = key.export_ec2_coordinates().unwrap();
    assert_eq!(x.as_slice(), &point.x().unwrap()[..]);
    assert_eq!(y.as_slice(), &point.y().unwrap()[..]);
    let mut duplicate_cose: Cbor = ciborium::from_reader(cose.as_slice()).unwrap();
    if let Cbor::Map(ref mut entries) = duplicate_cose {
        entries.push((Cbor::Integer(1.into()), Cbor::Integer(2.into())));
    }
    let mut duplicate_cose_bytes = Vec::new();
    ciborium::into_writer(&duplicate_cose, &mut duplicate_cose_bytes).unwrap();
    assert!(webauthn::CoseKey::from_stored(&duplicate_cose_bytes).is_err());
    let challenge = "challenge-123";
    let client =
        r#"{"type":"webauthn.create","challenge":"challenge-123","origin":"https://example.test"}"#;
    let mut auth_data = Sha256::digest(b"example.test").to_vec();
    auth_data.push(0x45); // UP, UV, AT
    auth_data.extend_from_slice(&5_u32.to_be_bytes());
    auth_data.extend_from_slice(&[0_u8; 16]);
    auth_data.extend_from_slice(&3_u16.to_be_bytes());
    auth_data.extend_from_slice(b"cid");
    auth_data.extend_from_slice(&cose);
    let mut signed = auth_data.clone();
    signed.extend_from_slice(&Sha256::digest(client.as_bytes()));
    let signature: Signature = signing_key.sign(&signed);
    let attestation = |algorithm: i64, signature: Vec<u8>| {
        let value = Cbor::Map(vec![
            (Cbor::Text("fmt".into()), Cbor::Text("packed".into())),
            (
                Cbor::Text("attStmt".into()),
                Cbor::Map(vec![
                    (Cbor::Text("alg".into()), Cbor::Integer(algorithm.into())),
                    (Cbor::Text("sig".into()), Cbor::Bytes(signature)),
                ]),
            ),
            (
                Cbor::Text("authData".into()),
                Cbor::Bytes(auth_data.clone()),
            ),
        ]);
        let mut encoded = Vec::new();
        ciborium::into_writer(&value, &mut encoded).unwrap();
        encoded
    };
    let valid = attestation(-7, signature.to_der().as_bytes().to_vec());
    let registration = webauthn::verify_registration(
        client,
        &valid,
        challenge,
        "example.test",
        "https://example.test",
        true,
    )
    .unwrap();
    assert_eq!(registration.credential_id, b"cid");
    let mut alias: Cbor = ciborium::from_reader(valid.as_slice()).unwrap();
    if let Cbor::Map(ref mut entries) = alias {
        entries.push((Cbor::Integer(1.into()), Cbor::Text("none".into())));
    }
    let mut alias_bytes = Vec::new();
    ciborium::into_writer(&alias, &mut alias_bytes).unwrap();
    assert!(
        webauthn::verify_registration(
            client,
            &alias_bytes,
            challenge,
            "example.test",
            "https://example.test",
            true
        )
        .is_err()
    );
    assert!(
        webauthn::verify_registration(
            client,
            &attestation(-257, signature.to_der().as_bytes().to_vec()),
            challenge,
            "example.test",
            "https://example.test",
            true
        )
        .is_err()
    );
    let mut forged = signature.to_der().as_bytes().to_vec();
    *forged.last_mut().unwrap() ^= 1;
    assert!(
        webauthn::verify_registration(
            client,
            &attestation(-7, forged),
            challenge,
            "example.test",
            "https://example.test",
            true
        )
        .is_err()
    );
}

#[test]
fn webauthn_rsa_cose_keys_verify_pkcs1_and_pss_signatures() {
    use ciborium::value::Value as Cbor;
    use rand::rngs::OsRng;
    use rsa::signature::{RandomizedSigner, SignatureEncoding};
    use rsa::{RsaPrivateKey, traits::PublicKeyParts};
    let private = RsaPrivateKey::new(&mut OsRng, 2048).unwrap();
    let public = private.to_public_key();
    let message = b"authenticator data plus client data hash";
    for algorithm in [-257, -37] {
        let encoded = Cbor::Map(vec![
            (Cbor::Integer(1.into()), Cbor::Integer(3.into())),
            (Cbor::Integer(3.into()), Cbor::Integer(algorithm.into())),
            (
                Cbor::Integer((-1).into()),
                Cbor::Bytes(public.n().to_bytes_be()),
            ),
            (
                Cbor::Integer((-2).into()),
                Cbor::Bytes(public.e().to_bytes_be()),
            ),
        ]);
        let mut raw = Vec::new();
        ciborium::into_writer(&encoded, &mut raw).unwrap();
        let key = webauthn::CoseKey::from_stored(&raw).unwrap();
        let signature = if algorithm == -257 {
            rsa::pkcs1v15::SigningKey::<sha2::Sha256>::new(private.clone())
                .sign_with_rng(&mut OsRng, message)
                .to_vec()
        } else {
            rsa::pss::SigningKey::<sha2::Sha256>::new(private.clone())
                .sign_with_rng(&mut OsRng, message)
                .to_vec()
        };
        assert!(key.verify(message, &signature));
        assert!(!key.verify(b"different", &signature));
        assert!(webauthn::CoseKey::from_stored(&[raw, vec![0]].concat()).is_err());
    }
}
