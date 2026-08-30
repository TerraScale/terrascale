use terrabase::encryption::{
    ArtifactCipher, Compression, DatabaseKey, EncryptionContext, EncryptionError,
    MAX_PLAINTEXT_BYTES, ProviderMode,
};

fn context(tenant: &str) -> EncryptionContext {
    EncryptionContext::new(
        "local",
        tenant,
        "01JDATABASE",
        "wal",
        "01JARTIFACT",
        "01JKEYVERSION",
        1,
        Compression::None,
    )
    .unwrap()
}

fn cipher(key: [u8; 32]) -> ArtifactCipher {
    ArtifactCipher::new(
        ProviderMode::LocalNonFips,
        DatabaseKey::from_bytes(key),
        context("01JTENANT"),
    )
    .unwrap()
}

#[test]
fn local_provider_is_explicit_and_production_fails_closed() {
    assert_eq!(ProviderMode::LocalNonFips.identity(), "aws-lc-rs:local-non-fips");
    let result = ArtifactCipher::new(
        ProviderMode::ProductionFips,
        DatabaseKey::from_bytes([7; 32]),
        context("01JTENANT"),
    );
    assert_eq!(result, Err(EncryptionError::ProductionProviderUnavailable));
}

#[test]
fn round_trip_and_repeatability_are_stable() {
    let plaintext = b"known-answer plaintext";
    let mut first = cipher([3; 32]);
    let mut second = cipher([3; 32]);
    let envelope = first.seal(plaintext).unwrap();
    let repeated = second.seal(plaintext).unwrap();
    assert_eq!(envelope, repeated);
    assert_eq!(first.open(&envelope).unwrap(), plaintext);
}

#[test]
fn wrong_key_aad_tag_and_truncation_are_rejected() {
    let mut writer = cipher([4; 32]);
    let envelope = writer.seal(b"authenticated bytes").unwrap();

    let mut wrong_key = cipher([5; 32]);
    assert_eq!(wrong_key.open(&envelope), Err(EncryptionError::AuthenticationFailed));

    let mut wrong_aad = ArtifactCipher::new(
        ProviderMode::LocalNonFips,
        DatabaseKey::from_bytes([4; 32]),
        context("01JOTHER TENANT"),
    )
    .unwrap();
    assert_eq!(wrong_aad.open(&envelope), Err(EncryptionError::AuthenticationFailed));

    let mut bad_tag = envelope.clone();
    *bad_tag.last_mut().unwrap() ^= 1;
    let mut reader = cipher([4; 32]);
    assert_eq!(reader.open(&bad_tag), Err(EncryptionError::AuthenticationFailed));

    let mut truncated = envelope;
    truncated.pop();
    assert_eq!(reader.open(&truncated), Err(EncryptionError::MalformedEnvelope));
}

#[test]
fn replay_and_ordinal_exhaustion_are_refused() {
    let mut writer = ArtifactCipher::with_next_ordinal(
        ProviderMode::LocalNonFips,
        DatabaseKey::from_bytes([9; 32]),
        context("01JTENANT"),
        (1u128 << 96) - 1,
    )
    .unwrap();
    let envelope = writer.seal(b"last ordinal").unwrap();
    assert_eq!(writer.next_ordinal(), 1u128 << 96);
    assert_eq!(writer.seal(b"overflow"), Err(EncryptionError::OrdinalExhausted));

    let mut reader = cipher([9; 32]);
    assert_eq!(reader.open(&envelope).unwrap(), b"last ordinal");
    assert_eq!(reader.open(&envelope), Err(EncryptionError::Replay));
}

#[test]
fn malformed_and_oversized_inputs_fail_before_crypto() {
    let mut c = cipher([1; 32]);
    assert_eq!(c.seal(&vec![0; MAX_PLAINTEXT_BYTES + 1]), Err(EncryptionError::SizeLimit));
    assert_eq!(c.open(b"TBE1"), Err(EncryptionError::MalformedEnvelope));
}

#[test]
fn generated_database_keys_are_redacted() {
    let a = DatabaseKey::generate().unwrap();
    let b = DatabaseKey::generate().unwrap();
    let mut first =
        ArtifactCipher::new(ProviderMode::LocalNonFips, a, context("01JTENANT")).unwrap();
    let mut second =
        ArtifactCipher::new(ProviderMode::LocalNonFips, b, context("01JTENANT")).unwrap();
    assert_ne!(first.seal(b"random key").unwrap(), second.seal(b"random key").unwrap());
    assert_eq!(format!("{:?}", DatabaseKey::from_bytes([0; 32])), "DatabaseKey(REDACTED)");
}
