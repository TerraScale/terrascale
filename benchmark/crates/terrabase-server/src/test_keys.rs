//! Test-only real Unix key delivery fixture; production has no raw key factory.
use std::collections::BTreeMap;
use std::io::{Read, Write};
use std::os::unix::fs::{MetadataExt, PermissionsExt};
use std::os::unix::net::UnixListener;
use std::sync::{
    Arc,
    atomic::{AtomicBool, Ordering},
};
use std::time::Duration;
use terrabase::encryption::{
    DatabaseKeySource, DatabaseKeyVersion, KeyPurpose, KeyScope, KeyVersionMetadata,
    OciVaultDatabaseKeys, OciVaultKeyDelivery,
};
use terrabase::member::{DatabaseId, DatabaseScope};

pub(crate) fn storage_keys(
    scope: &DatabaseScope,
) -> BTreeMap<DatabaseId, Arc<dyn DatabaseKeySource>> {
    keys_for(scope, KeyPurpose::Data)
}

pub(crate) fn backup_keys(scope: &DatabaseScope) -> Arc<dyn DatabaseKeySource> {
    keys_for(scope, KeyPurpose::Backup).into_values().next().unwrap()
}

fn keys_for(
    scope: &DatabaseScope,
    purpose: KeyPurpose,
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
    let provider = OciVaultKeyDelivery::new(socket.clone(), stat.uid(), stat.gid()).unwrap();
    let version = DatabaseKeyVersion::from_text("01ARZ3NDEKTSV4RRFFQ69G5FB0").unwrap();
    let metadata = KeyVersionMetadata::new(
        KeyScope::new_with_project(
            scope.environment().as_str(),
            scope.tenant(),
            scope.project(),
            scope.database(),
            purpose,
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

#[cfg(target_os = "linux")]
pub(crate) fn storage_keys_with_provider(
    scope: &DatabaseScope,
    provider: OciVaultKeyDelivery,
) -> BTreeMap<DatabaseId, Arc<dyn DatabaseKeySource>> {
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
    let source = OciVaultDatabaseKeys::new(provider, metadata, scope.region()).unwrap();
    BTreeMap::from([(scope.database().into(), Arc::new(source) as Arc<dyn DatabaseKeySource>)])
}

/// A retained Unix key-delivery fixture for admission replay tests.
/// The listener remains available for every key lease request until dropped.
#[cfg(target_os = "linux")]
pub(crate) struct KeyDeliveryFixture {
    pub(crate) provider: OciVaultKeyDelivery,
    pub(crate) socket: std::path::PathBuf,
    pub(crate) uid: u32,
    pub(crate) gid: u32,
    directory: std::path::PathBuf,
    stop: Arc<AtomicBool>,
    server: Option<std::thread::JoinHandle<()>>,
}

#[cfg(target_os = "linux")]
impl Drop for KeyDeliveryFixture {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::Release);
        if let Some(server) = self.server.take() {
            let _ = server.join();
        }
        let _ = std::fs::remove_dir_all(&self.directory);
    }
}

#[cfg(target_os = "linux")]
pub(crate) fn key_delivery() -> KeyDeliveryFixture {
    let directory = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .parent()
        .unwrap()
        .join("target")
        .join(format!("tk-replay-{}", terrabase::identity::generate()));
    std::fs::create_dir_all(&directory).unwrap();
    std::fs::set_permissions(&directory, std::fs::Permissions::from_mode(0o710)).unwrap();
    let socket = directory.join("k.sock");
    let listener = UnixListener::bind(&socket).unwrap();
    listener.set_nonblocking(true).unwrap();
    std::fs::set_permissions(&socket, std::fs::Permissions::from_mode(0o660)).unwrap();
    let stat = std::fs::metadata(&socket).unwrap();
    let provider = OciVaultKeyDelivery::new(socket.clone(), stat.uid(), stat.gid()).unwrap();
    let stop = Arc::new(AtomicBool::new(false));
    let server_stop = stop.clone();
    let server = std::thread::spawn(move || {
        while !server_stop.load(Ordering::Acquire) {
            let Ok((mut stream, _)) = listener.accept() else {
                std::thread::sleep(Duration::from_millis(1));
                continue;
            };
            let _ = stream.set_read_timeout(Some(Duration::from_millis(100)));
            let mut size = [0; 4];
            if stream.read_exact(&mut size).is_err() {
                continue;
            }
            let size = usize::try_from(u32::from_be_bytes(size)).unwrap();
            if size > 2048 {
                continue;
            }
            let mut request = vec![0; size];
            if stream.read_exact(&mut request).is_err()
                || request.get(..5) != Some(&b"TBKL\x01"[..])
            {
                continue;
            }
            let mut response = 68u32.to_be_bytes().to_vec();
            response.extend_from_slice(b"TBKL\x01\x00");
            response.extend_from_slice(&1800u32.to_be_bytes());
            response.extend_from_slice(b"01ARZ3NDEKTSV4RRFFQ69G5FB0");
            response.extend_from_slice(&[31; 32]);
            let _ = stream.write_all(&response);
        }
    });
    KeyDeliveryFixture {
        provider,
        socket,
        uid: stat.uid(),
        gid: stat.gid(),
        directory,
        stop,
        server: Some(server),
    }
}
