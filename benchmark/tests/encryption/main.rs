use terrabase::encryption::{
    ArtifactCipher, ArtifactKind, Compression, DatabaseKey, DatabaseKeyLease, DatabaseKeyVersion,
    EncryptionContext, EncryptionError, KeyProviderIdentity, KeyPurpose, KeyRotation, KeyScope,
    KeyVersionMetadata, KeyWrappingProvider, LocalFileKeyProvider, MAX_PLAINTEXT_BYTES,
    ProductionCapabilityStatus, ProviderCapability, ProviderMode, UnconfiguredOciVaultProvider,
    WrappedDatabaseKey, production_capability_status,
};
use terrabase::identity::{DatabaseUlid, TenantUlid, Ulid};
use terrabase::regions::RegionCode;

#[test]
fn aws_lc_aes_256_gcm_known_answer() {
    let key = aws_lc_rs::aead::UnboundKey::new(&aws_lc_rs::aead::AES_256_GCM, &[0; 32]).unwrap();
    let key = aws_lc_rs::aead::LessSafeKey::new(key);
    let nonce = aws_lc_rs::aead::Nonce::assume_unique_for_key([0; 12]);
    let mut ciphertext = [0; 16].to_vec();
    key.seal_in_place_append_tag(nonce, aws_lc_rs::aead::Aad::empty(), &mut ciphertext).unwrap();
    assert_eq!(
        ciphertext,
        [
            0xce, 0xa7, 0x40, 0x3d, 0x4d, 0x60, 0x6b, 0x6e, 0x07, 0x4e, 0xc5, 0xd3, 0xba, 0xf3,
            0x9d, 0x18, 0xd0, 0xd1, 0xc8, 0xa7, 0x99, 0x99, 0x6b, 0xf0, 0x26, 0x5b, 0x98, 0xb5,
            0xd4, 0x8a, 0xb9, 0x19,
        ]
    );
}

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
fn local_provider_is_explicit_and_production_fails_closed() {
    assert_eq!(ProviderMode::LocalFile.identity(), "aws-lc-rs:local-file-non-fips");
    assert_eq!(ProviderMode::ProductionFips.identity(), "aws-lc-rs:production-fips");
    assert!(matches!(
        ArtifactCipher::new(
            ProviderMode::ProductionFips,
            DatabaseKey::from_bytes([7; 32]),
            context("01JTENANT"),
        ),
        Err(EncryptionError::ProductionProviderUnavailable)
    ));
}

#[test]
fn key_derivation_domains_environment_tenant_and_database() {
    let plaintext = b"same ordinal, distinct scope";
    let scopes = [
        context("01JTENANT"),
        EncryptionContext::new(
            "production",
            "01JTENANT",
            "01JDATABASE",
            "wal",
            "01JARTIFACT",
            "01JKEYVERSION",
            1,
            Compression::None,
        )
        .unwrap(),
        EncryptionContext::new(
            "local",
            "01JOTHER",
            "01JDATABASE",
            "wal",
            "01JARTIFACT",
            "01JKEYVERSION",
            1,
            Compression::None,
        )
        .unwrap(),
        EncryptionContext::new(
            "local",
            "01JTENANT",
            "01JOTHERDB",
            "wal",
            "01JARTIFACT",
            "01JKEYVERSION",
            1,
            Compression::None,
        )
        .unwrap(),
    ];
    let mut ciphers = scopes.map(|scope| {
        ArtifactCipher::new(ProviderMode::LocalFile, DatabaseKey::from_bytes([0x42; 32]), scope)
            .unwrap()
    });
    let envelopes = ciphers.each_mut().map(|cipher| cipher.seal(plaintext).unwrap());
    let ciphertext_start = |envelope: &[u8]| {
        let aad_len = u32::from_be_bytes(envelope[17..21].try_into().unwrap()) as usize;
        29 + aad_len
    };
    let first = ciphertext_start(&envelopes[0]);
    let first_ciphertext = &envelopes[0][first..first + plaintext.len()];
    for envelope in &envelopes[1..] {
        let start = ciphertext_start(envelope);
        assert_ne!(first_ciphertext, &envelope[start..start + plaintext.len()]);
    }
}

#[cfg(feature = "production-fips")]
#[test]
fn production_profile_links_an_operational_aws_lc_fips_module() {
    aws_lc_rs::try_fips_mode().expect("the production AWS-LC provider is in FIPS mode");
}

#[test]
fn sequential_ordinals_produce_unique_envelopes() {
    let plaintext = b"known-answer plaintext";
    let mut writer = cipher([3; 32]);
    let first = writer.seal(plaintext).unwrap();
    let second = writer.seal(plaintext).unwrap();
    assert_ne!(first, second);
    assert_eq!(writer.open(&first).unwrap(), plaintext);
    assert_eq!(writer.open(&second).unwrap(), plaintext);
}

#[test]
fn reserved_ordinals_survive_restart_without_nonce_reuse() {
    let mut writer = cipher([6; 32]);
    let abandoned = writer.reserve_ordinal().unwrap();
    assert_eq!(abandoned.ordinal(), 0);
    assert_eq!(abandoned.next_ordinal(), 1);

    let mut recovered = ArtifactCipher::with_next_ordinal(
        ProviderMode::LocalFile,
        DatabaseKey::from_bytes([6; 32]),
        context("01JTENANT"),
        abandoned.next_ordinal(),
    )
    .unwrap();
    let after_restart = recovered.seal(b"after restart").unwrap();
    assert_eq!(recovered.next_ordinal(), 2);
    assert_eq!(recovered.open(&after_restart).unwrap(), b"after restart");
}

#[test]
fn restart_at_ordinal_exhaustion_allows_reads_but_not_writes() {
    let mut writer = ArtifactCipher::with_next_ordinal(
        ProviderMode::LocalFile,
        DatabaseKey::from_bytes([0x2a; 32]),
        context("01JTENANT"),
        (1u128 << 96) - 1,
    )
    .unwrap();
    let envelope = writer.seal(b"final ordinal").unwrap();
    assert_eq!(writer.next_ordinal(), 1u128 << 96);

    let mut recovered = ArtifactCipher::with_next_ordinal(
        ProviderMode::LocalFile,
        DatabaseKey::from_bytes([0x2a; 32]),
        context("01JTENANT"),
        1u128 << 96,
    )
    .unwrap();
    assert_eq!(recovered.open(&envelope).unwrap(), b"final ordinal");
    assert_eq!(recovered.seal(b"after exhaustion"), Err(EncryptionError::OrdinalExhausted));
}

#[test]
fn wrong_key_aad_tag_and_truncation_are_rejected() {
    let mut writer = cipher([4; 32]);
    let envelope = writer.seal(b"authenticated bytes").unwrap();

    let wrong_key = cipher([5; 32]);
    assert_eq!(wrong_key.open(&envelope), Err(EncryptionError::AuthenticationFailed));

    let wrong_aad = ArtifactCipher::new(
        ProviderMode::LocalFile,
        DatabaseKey::from_bytes([4; 32]),
        context("01JOTHER TENANT"),
    )
    .unwrap();
    assert_eq!(wrong_aad.open(&envelope), Err(EncryptionError::AuthenticationFailed));

    let mut bad_tag = envelope.clone();
    *bad_tag.last_mut().unwrap() ^= 1;
    let reader = cipher([4; 32]);
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

    let reader = cipher([9; 32]);
    assert_eq!(reader.open(&envelope).unwrap(), b"last ordinal");
    assert_eq!(reader.open(&envelope).unwrap(), b"last ordinal");

    let mut guard = terrabase::encryption::ReplayGuard::new();
    assert_eq!(reader.open_with_replay_guard(&envelope, &mut guard).unwrap(), b"last ordinal");
    assert_eq!(reader.open_with_replay_guard(&envelope, &mut guard), Err(EncryptionError::Replay));
}

#[test]
fn ordinal_reservation_consumes_nonce_before_retry() {
    let mut writer = cipher([0x31; 32]);
    let reservation = writer.reserve_ordinal().unwrap();
    assert_eq!(reservation.ordinal(), 0);
    assert_eq!(reservation.next_ordinal(), 1);
    assert_eq!(writer.next_ordinal(), reservation.next_ordinal());
    let envelope = writer.seal_reserved(reservation, b"reserved").unwrap();
    assert_eq!(writer.open(&envelope).unwrap(), b"reserved");
    assert_eq!(
        writer.seal_reserved(reservation, b"retry"),
        Err(EncryptionError::OrdinalReservationInvalid)
    );
}

#[test]
fn malformed_and_oversized_inputs_fail_before_crypto() {
    let mut c = cipher([1; 32]);
    assert_eq!(c.seal(&vec![0; MAX_PLAINTEXT_BYTES + 1]), Err(EncryptionError::SizeLimit));
    assert_eq!(c.next_ordinal(), 1, "a failed seal must still consume its nonce");
    assert_eq!(c.open(b"TBE1"), Err(EncryptionError::MalformedEnvelope));
}

#[test]
fn key_versions_and_purposes_are_bound_to_artifact_keys() {
    let scope = typed_scope(KeyPurpose::Data);
    let artifact = Ulid::from_text("01ARZ3NDEKTSV4RRFFQ69G5FAV").unwrap();
    let first_version = DatabaseKeyVersion::from_text("01ARZ3NDEKTSV4RRFFQ69G5FAW").unwrap();
    let next_version = DatabaseKeyVersion::from_text("01ARZ3NDEKTSV4RRFFQ69G5FAX").unwrap();
    let first_context = EncryptionContext::for_artifact(
        &scope,
        ArtifactKind::Wal,
        artifact,
        first_version,
        1,
        Compression::None,
    )
    .unwrap();
    let next_context = EncryptionContext::for_artifact(
        &scope,
        ArtifactKind::Wal,
        artifact,
        next_version,
        1,
        Compression::None,
    )
    .unwrap();
    let mut first = ArtifactCipher::new(
        ProviderMode::LocalFile,
        DatabaseKey::from_bytes([12; 32]),
        first_context,
    )
    .unwrap();
    let next = ArtifactCipher::new(
        ProviderMode::LocalFile,
        DatabaseKey::from_bytes([12; 32]),
        next_context,
    )
    .unwrap();
    let envelope = first.seal(b"old version").unwrap();
    assert_eq!(next.open(&envelope), Err(EncryptionError::AuthenticationFailed));

    assert_eq!(
        EncryptionContext::for_artifact(
            &scope,
            ArtifactKind::Backup,
            artifact,
            next_version,
            1,
            Compression::None,
        ),
        Err(EncryptionError::InvalidKeyMetadata)
    );
}

#[test]
fn rotation_preserves_scope_and_advances_version() {
    let scope = typed_scope(KeyPurpose::Data);
    let current = KeyVersionMetadata::new(
        scope.clone(),
        DatabaseKeyVersion::from_text("01ARZ3NDEKTSV4RRFFQ69G5FAY").unwrap(),
    );
    let next = KeyVersionMetadata::new(
        scope.clone(),
        DatabaseKeyVersion::from_text("01ARZ3NDEKTSV4RRFFQ69G5FAZ").unwrap(),
    );
    let rotation = KeyRotation::new(current.clone(), next.clone()).unwrap();
    assert_eq!(rotation.current(), &current);
    assert_eq!(rotation.next(), &next);
    assert_eq!(
        KeyRotation::new(current.clone(), current.clone()),
        Err(EncryptionError::InvalidKeyRotation)
    );

    let backup_scope = typed_scope(KeyPurpose::Backup);
    assert_eq!(
        KeyRotation::new(current, KeyVersionMetadata::new(backup_scope, next.version()),),
        Err(EncryptionError::InvalidKeyRotation)
    );
}

#[test]
fn oci_vault_seam_is_unconfigured_and_fails_closed() {
    let provider = UnconfiguredOciVaultProvider::new();
    assert_eq!(provider.identity(), KeyProviderIdentity::OciVault);
    assert_eq!(provider.capability(), ProviderCapability::Unconfigured);
    assert!(!production_capability_status(&provider).is_production_ready());
    #[cfg(feature = "local-crypto")]
    assert_eq!(
        production_capability_status(&provider),
        ProductionCapabilityStatus::LocalCryptoSelected
    );
    #[cfg(feature = "production-fips")]
    assert_eq!(
        production_capability_status(&provider),
        ProductionCapabilityStatus::OciVaultUnconfigured
    );

    let metadata = KeyVersionMetadata::new(
        typed_scope(KeyPurpose::Data),
        DatabaseKeyVersion::from_text("01ARZ3NDEKTSV4RRFFQ69G5FB0").unwrap(),
    );
    assert!(matches!(
        provider.wrap_database_key(&metadata, RegionCode::GRU, &DatabaseKey::from_bytes([8; 32])),
        Err(EncryptionError::KeyProviderUnconfigured)
    ));

    let wrapper = WrappedDatabaseKey::new(
        metadata,
        RegionCode::GRU,
        KeyProviderIdentity::OciVault,
        [1; 32],
        vec![2; 48],
    )
    .unwrap();
    assert!(matches!(
        provider.unwrap_database_key(&wrapper, std::time::Duration::from_secs(30)),
        Err(EncryptionError::KeyProviderUnconfigured)
    ));
    assert!(!format!("{wrapper:?}").contains("[2, 2"));
}

#[test]
fn local_lease_cannot_claim_production_provenance() {
    let scope = typed_scope(KeyPurpose::Data);
    let version = DatabaseKeyVersion::from_text("01ARZ3NDEKTSV4RRFFQ69G5FB0").unwrap();
    let metadata = KeyVersionMetadata::new(scope.clone(), version);
    let lease = DatabaseKeyLease::new(
        metadata,
        DatabaseKey::from_bytes([0x55; 32]),
        std::time::Duration::from_secs(30),
    )
    .unwrap();
    let context = EncryptionContext::new(
        scope.environment(),
        scope.tenant().to_text(),
        scope.database().to_text(),
        "wal",
        "01ARZ3NDEKTSV4RRFFQ69G5FAV",
        version.to_text(),
        1,
        Compression::None,
    )
    .unwrap();
    assert!(matches!(
        ArtifactCipher::from_database_key_lease(&lease, context, 0),
        Err(EncryptionError::ProductionProviderUnavailable)
    ));
}

#[test]
fn database_key_lease_expires_and_redacts_key_material() {
    let metadata = KeyVersionMetadata::new(
        typed_scope(KeyPurpose::Data),
        DatabaseKeyVersion::from_text("01ARZ3NDEKTSV4RRFFQ69G5FB3").unwrap(),
    );
    let lease = DatabaseKeyLease::new(
        metadata,
        DatabaseKey::from_bytes([0x5a; 32]),
        std::time::Duration::from_nanos(1),
    )
    .unwrap();
    std::thread::sleep(std::time::Duration::from_millis(1));
    assert_eq!(lease.with_database_key(|_| 1_u8), Err(EncryptionError::KeyLeaseExpired));
    assert!(!format!("{lease:?}").contains("5a"));
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

#[cfg(unix)]
#[test]
fn local_file_key_rejects_symlink() {
    use std::os::unix::fs::symlink;

    let directory = unique_directory("symlink");
    let provider = LocalFileKeyProvider::at_engine_location(&directory);
    std::fs::create_dir_all(provider.path().parent().unwrap()).unwrap();
    let target = directory.join("target.key");
    std::fs::write(&target, [0u8; 32]).unwrap();
    symlink(&target, provider.path()).unwrap();
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

fn typed_scope(purpose: KeyPurpose) -> KeyScope {
    KeyScope::new(
        "production",
        TenantUlid::from_text("01ARZ3NDEKTSV4RRFFQ69G5FB1").unwrap(),
        DatabaseUlid::from_text("01ARZ3NDEKTSV4RRFFQ69G5FB2").unwrap(),
        purpose,
    )
    .unwrap()
}
