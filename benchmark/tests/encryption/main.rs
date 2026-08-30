use terrabase::encryption::{
    ArtifactCipher, Compression, DatabaseKey, EncryptionContext, EncryptionError,
    LocalFileKeyProvider, MAX_PLAINTEXT_BYTES, ProviderMode,
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
    ArtifactCipher::new(ProviderMode::LocalFile, DatabaseKey::from_bytes(key), context("01JTENANT"))
        .unwrap()
}

#[test]
fn selected_provider_is_fips_and_provider_identities_are_explicit() {
    assert_eq!(ProviderMode::LocalFile.identity(), "aws-lc-fips:local-file");
    assert_eq!(ProviderMode::ProductionFips.identity(), "aws-lc-fips:oci-vault");
    aws_lc_rs::try_fips_mode().expect("the selected AWS-LC provider is in FIPS mode");
    ArtifactCipher::new(
        ProviderMode::ProductionFips,
        DatabaseKey::from_bytes([7; 32]),
        context("01JTENANT"),
    )
    .expect("production mode accepts the linked FIPS provider");
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
        ProviderMode::LocalFile,
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
        ProviderMode::LocalFile,
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
    let mut first = ArtifactCipher::new(ProviderMode::LocalFile, a, context("01JTENANT")).unwrap();
    let mut second = ArtifactCipher::new(ProviderMode::LocalFile, b, context("01JTENANT")).unwrap();
    assert_ne!(first.seal(b"random key").unwrap(), second.seal(b"random key").unwrap());
    assert_eq!(format!("{:?}", DatabaseKey::from_bytes([0; 32])), "DatabaseKey(REDACTED)");
}

#[test]
fn local_file_key_persists_without_exposing_material() {
    let directory = unique_directory("persistence");
    let provider = LocalFileKeyProvider::at_engine_location(&directory);
    assert_eq!(provider.identity(), "local-file");

    let first = provider.load_or_create().unwrap();
    let second = provider.load_or_create().unwrap();
    let mut first_cipher =
        ArtifactCipher::new(ProviderMode::LocalFile, first, context("01JTENANT")).unwrap();
    let mut second_cipher =
        ArtifactCipher::new(ProviderMode::LocalFile, second, context("01JTENANT")).unwrap();
    assert_eq!(
        first_cipher.seal(b"stable local key").unwrap(),
        second_cipher.seal(b"stable local key").unwrap()
    );
    assert_eq!(std::fs::metadata(provider.path()).unwrap().len(), 32);

    std::fs::remove_dir_all(directory).unwrap();
}

#[cfg(unix)]
#[test]
fn local_file_key_rejects_broad_permissions() {
    use std::os::unix::fs::PermissionsExt as _;

    let directory = unique_directory("permissions");
    let provider = LocalFileKeyProvider::at_engine_location(&directory);
    drop(provider.load_or_create().unwrap());
    std::fs::set_permissions(provider.path(), std::fs::Permissions::from_mode(0o644)).unwrap();
    assert!(matches!(provider.load_or_create(), Err(EncryptionError::UnsafeLocalKeyFile)));

    std::fs::remove_dir_all(directory).unwrap();
}

fn unique_directory(label: &str) -> std::path::PathBuf {
    use std::sync::atomic::{AtomicU64, Ordering};

    static NEXT: AtomicU64 = AtomicU64::new(1);
    std::env::temp_dir().join(format!(
        "terrabase-encryption-{label}-{}-{}",
        std::process::id(),
        NEXT.fetch_add(1, Ordering::Relaxed)
    ))
}
