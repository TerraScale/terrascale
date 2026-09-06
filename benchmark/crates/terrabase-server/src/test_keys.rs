//! Test-only real Unix key delivery fixture; production has no raw key factory.
use std::collections::BTreeMap;
use std::io::{Read, Write};
use std::os::unix::fs::{MetadataExt, PermissionsExt};
use std::os::unix::net::UnixListener;
use std::sync::Arc;
use terrabase::encryption::{
    DatabaseKeySource, DatabaseKeyVersion, KeyPurpose, KeyScope, KeyVersionMetadata,
    OciVaultDatabaseKeys, OciVaultKeyDelivery,
};
use terrabase::member::{DatabaseId, DatabaseScope};

pub(crate) fn storage_keys(
    scope: &DatabaseScope,
) -> BTreeMap<DatabaseId, Arc<dyn DatabaseKeySource>> {
    let directory = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .parent()
        .unwrap()
        .join("target")
        .join(format!("tk-{}", terrabase::identity::generate()));
    std::fs::create_dir_all(&directory).unwrap();
    std::fs::set_permissions(&directory, std::fs::Permissions::from_mode(0o710)).unwrap();
    let socket = directory.join("k.sock");
    let listener = UnixListener::bind(&socket).unwrap();
    std::fs::set_permissions(&socket, std::fs::Permissions::from_mode(0o660)).unwrap();
    let stat = std::fs::metadata(&socket).unwrap();
    let provider = OciVaultKeyDelivery::new(socket, stat.uid(), stat.gid()).unwrap();
    let version = DatabaseKeyVersion::from_text("01ARZ3NDEKTSV4RRFFQ69G5FB0").unwrap();
    let metadata = KeyVersionMetadata::new(
        KeyScope::new_with_project(
            scope.environment().as_str(),
            scope.tenant(),
            scope.project(),
            scope.database(),
            KeyPurpose::Data,
        )
        .unwrap(),
        version,
    );
    let server = std::thread::spawn(move || {
        let (mut stream, _) = listener.accept().unwrap();
        let mut size = [0; 4];
        stream.read_exact(&mut size).unwrap();
        let size = usize::try_from(u32::from_be_bytes(size)).unwrap();
        assert!(size <= 2048);
        let mut request = vec![0; size];
        stream.read_exact(&mut request).unwrap();
        assert_eq!(&request[..5], b"TBKL\x01");
        let mut response = 68u32.to_be_bytes().to_vec();
        response.extend_from_slice(b"TBKL\x01\x00");
        response.extend_from_slice(&1800u32.to_be_bytes());
        response.extend_from_slice(version.to_text().as_bytes());
        response.extend_from_slice(&[31; 32]);
        stream.write_all(&response).unwrap();
    });
    let source = OciVaultDatabaseKeys::new(provider, metadata, scope.region()).unwrap();
    server.join().unwrap();
    std::fs::remove_dir_all(directory).unwrap();
    BTreeMap::from([(scope.database().into(), Arc::new(source) as Arc<dyn DatabaseKeySource>)])
}
