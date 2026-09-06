//! Native SQLite locking and WAL-index operations with encrypted data I/O.
//!
//! SQLite owns the outer file allocation. Each successful xOpen places a boxed
//! Rust state pointer there, and xClose consumes it exactly once. The state owns
//! both its native file allocation and an Arc keeping this registered VFS alive.
#![allow(unsafe_code, reason = "SQLite's VFS ABI requires narrowly scoped C callbacks")]

use super::encrypted_file::EncryptedFile;
use crate::encryption::{ArtifactKind, DatabaseKeySource};
use rusqlite::ffi;
use std::{
    cell::UnsafeCell,
    collections::HashMap,
    ffi::{CStr, CString, c_char, c_int, c_void},
    io,
    path::{Path, PathBuf},
    ptr,
    sync::{
        Arc, LazyLock, Mutex, Weak,
        atomic::{AtomicU64, Ordering},
    },
};

static NEXT_ID: AtomicU64 = AtomicU64::new(1);
static REGISTRATIONS: LazyLock<Mutex<HashMap<PathBuf, Weak<EncryptedVfs>>>> =
    LazyLock::new(|| Mutex::new(HashMap::new()));

pub(crate) fn lookup(path: &Path) -> Option<Arc<EncryptedVfs>> {
    let path = path.parent()?.canonicalize().ok()?.join(path.file_name()?);
    let registrations = REGISTRATIONS.lock().ok()?;
    registrations.values().filter_map(Weak::upgrade).find(|vfs| vfs.kind(&path).is_some())
}

/// A non-default VFS scoped to one encrypted database and its journals.
/// Keep this guard while opening connections; open files retain their own guard.
pub(crate) struct EncryptedVfs {
    vfs: UnsafeCell<ffi::sqlite3_vfs>,
    native: *mut ffi::sqlite3_vfs,
    name: CString,
    database: PathBuf,
    keys: Arc<dyn DatabaseKeySource>,
    files: Mutex<HashMap<PathBuf, Weak<Mutex<EncryptedFile>>>>,
}
// The native VFS is SQLite's process-lifetime, internally synchronized default.
// Registration fields are immutable after publication, except pNext which only
// SQLite mutates under its registry mutex. Rust file state has its own mutex.
unsafe impl Send for EncryptedVfs {}
unsafe impl Sync for EncryptedVfs {}

impl EncryptedVfs {
    pub(crate) fn register(
        database: &Path,
        keys: Arc<dyn DatabaseKeySource>,
    ) -> io::Result<Arc<Self>> {
        let parent =
            database.parent().ok_or_else(|| io::Error::other("database parent missing"))?;
        let database = parent
            .canonicalize()?
            .join(database.file_name().ok_or_else(|| io::Error::other("database name missing"))?);
        let mut registrations = REGISTRATIONS
            .lock()
            .map_err(|_| io::Error::other("encrypted VFS registry poisoned"))?;
        registrations.retain(|_, registration| registration.strong_count() != 0);
        if let Some(existing) = registrations.get(&database).and_then(Weak::upgrade) {
            if Arc::ptr_eq(&existing.keys, &keys) {
                return Ok(existing);
            }
            return Err(io::Error::other("database already registered with another key source"));
        }
        let name = CString::new(format!(
            "terrabase-encrypted-{}",
            NEXT_ID.fetch_add(1, Ordering::Relaxed)
        ))
        .unwrap();
        // SQLite initializes the native registry before returning this pointer.
        let native = unsafe { ffi::sqlite3_vfs_find(ptr::null()) };
        if native.is_null() {
            return Err(io::Error::other("SQLite native VFS unavailable"));
        }
        let mut vfs = unsafe { *native };
        vfs.iVersion = 2;
        vfs.szOsFile = std::mem::size_of::<WrappedFile>() as c_int;
        vfs.pNext = ptr::null_mut();
        vfs.zName = name.as_ptr();
        vfs.xOpen = Some(open);
        vfs.xDelete = Some(delete);
        vfs.xAccess = Some(access);
        vfs.xFullPathname = Some(full_pathname);
        // Dynamic extensions and system-call replacement are not exposed.
        vfs.xDlOpen = None;
        vfs.xDlError = None;
        vfs.xDlSym = None;
        vfs.xDlClose = None;
        vfs.xRandomness = Some(randomness);
        vfs.xSleep = Some(sleep);
        vfs.xCurrentTime = Some(current_time);
        vfs.xGetLastError = Some(last_error);
        vfs.xCurrentTimeInt64 = Some(current_time_int64);
        vfs.xSetSystemCall = None;
        vfs.xGetSystemCall = None;
        vfs.xNextSystemCall = None;
        let registration = Arc::new(Self {
            vfs: UnsafeCell::new(vfs),
            native,
            name,
            database,
            keys,
            files: Mutex::new(HashMap::new()),
        });
        unsafe {
            (*registration.vfs.get()).pAppData = Arc::as_ptr(&registration).cast_mut().cast();
            if ffi::sqlite3_vfs_register(registration.vfs.get(), 0) != ffi::SQLITE_OK {
                return Err(io::Error::other("register encrypted SQLite VFS"));
            }
        }
        registrations.insert(registration.database.clone(), Arc::downgrade(&registration));
        Ok(registration)
    }

    pub(crate) fn keys(&self) -> Arc<dyn DatabaseKeySource> {
        self.keys.clone()
    }

    pub(crate) fn name(&self) -> &str {
        self.name.to_str().expect("generated ASCII VFS name")
    }

    fn kind(&self, path: &Path) -> Option<ArtifactKind> {
        if path == self.database {
            return Some(ArtifactKind::Database);
        }
        let mut wal = self.database.as_os_str().to_os_string();
        wal.push("-wal");
        let mut journal = self.database.as_os_str().to_os_string();
        journal.push("-journal");
        (path == Path::new(&wal) || path == Path::new(&journal)).then_some(ArtifactKind::Wal)
    }

    pub(crate) fn shared_file(&self, path: &Path) -> io::Result<Arc<Mutex<EncryptedFile>>> {
        self.shared_file_with_create(path, true)
    }

    fn shared_file_with_create(
        &self,
        path: &Path,
        allow_create: bool,
    ) -> io::Result<Arc<Mutex<EncryptedFile>>> {
        let path = path
            .parent()
            .ok_or_else(|| io::Error::other("database parent missing"))?
            .canonicalize()?
            .join(path.file_name().ok_or_else(|| io::Error::other("database name missing"))?);
        let path = path.as_path();
        let kind = self
            .kind(path)
            .ok_or_else(|| io::Error::other("encrypted VFS path outside database"))?;
        let mut files =
            self.files.lock().map_err(|_| io::Error::other("encrypted file registry poisoned"))?;
        if let Some(file) = files.get(path).and_then(Weak::upgrade) {
            return Ok(file);
        }
        let file = Arc::new(Mutex::new(if allow_create {
            EncryptedFile::open(path, self.keys.clone(), kind)?
        } else {
            EncryptedFile::open_existing(path, self.keys.clone(), kind)?
        }));
        files.insert(path.to_path_buf(), Arc::downgrade(&file));
        Ok(file)
    }
}
impl Drop for EncryptedVfs {
    fn drop(&mut self) {
        unsafe {
            ffi::sqlite3_vfs_unregister(self.vfs.get());
        }
    }
}

#[repr(C)]
struct WrappedFile {
    base: ffi::sqlite3_file,
    state: *mut FileState,
}
struct FileState {
    native: *mut ffi::sqlite3_file,
    file: Arc<Mutex<EncryptedFile>>,
    _registration: Arc<EncryptedVfs>,
    readonly: bool,
}
impl Drop for FileState {
    fn drop(&mut self) {
        unsafe {
            ffi::sqlite3_free(self.native.cast());
        }
    }
}

// Every caller below is a SQLite ABI callback: SQLite guarantees valid buffers,
// alignment, and lifetimes for the supplied pointers. No reference to file state
// escapes a callback, and Rust panics are converted to SQLite I/O failures.
fn boundary(f: impl FnOnce() -> c_int) -> c_int {
    std::panic::catch_unwind(std::panic::AssertUnwindSafe(f)).unwrap_or(ffi::SQLITE_IOERR)
}
unsafe fn registration<'a>(vfs: *mut ffi::sqlite3_vfs) -> &'a EncryptedVfs {
    unsafe { &*((*vfs).pAppData.cast::<EncryptedVfs>()) }
}
unsafe fn state<'a>(file: *mut ffi::sqlite3_file) -> &'a FileState {
    unsafe { &*((*file.cast::<WrappedFile>()).state) }
}
unsafe fn path(name: *const c_char) -> Option<PathBuf> {
    if name.is_null() {
        return None;
    }
    #[cfg(unix)]
    {
        use std::os::unix::ffi::OsStrExt;
        Some(PathBuf::from(std::ffi::OsStr::from_bytes(unsafe { CStr::from_ptr(name) }.to_bytes())))
    }
    #[cfg(not(unix))]
    {
        unsafe { CStr::from_ptr(name) }.to_str().ok().map(PathBuf::from)
    }
}
fn backing(path: &Path) -> PathBuf {
    let mut name = path.as_os_str().to_os_string();
    name.push(".tbe");
    PathBuf::from(name)
}
fn status(result: io::Result<()>, error: c_int) -> c_int {
    result.map_or(error, |()| ffi::SQLITE_OK)
}

unsafe extern "C" fn open(
    vfs: *mut ffi::sqlite3_vfs,
    name: *const c_char,
    output: *mut ffi::sqlite3_file,
    flags: c_int,
    out_flags: *mut c_int,
) -> c_int {
    boundary(|| unsafe {
        (*output).pMethods = ptr::null();
        let registry = registration(vfs);
        let Some(path) = path(name) else {
            return ffi::SQLITE_CANTOPEN;
        };
        if registry.kind(&path).is_none() || flags & ffi::SQLITE_OPEN_DELETEONCLOSE != 0 {
            return ffi::SQLITE_CANTOPEN;
        }
        // Opening a plaintext database as encrypted must never replace it.
        if std::fs::metadata(&path).is_ok_and(|metadata| {
            metadata.len() != 0 || (path == registry.database && !backing(&path).exists())
        }) {
            // A main stub without its companion is missing durable state.
            // Empty auxiliary stubs can remain after a crash during deletion;
            // SQLite may recreate those disposable files with CREATE.
            return ffi::SQLITE_NOTADB;
        }
        if flags & ffi::SQLITE_OPEN_CREATE == 0 && !backing(&path).exists() {
            return ffi::SQLITE_CANTOPEN;
        }
        let native =
            ffi::sqlite3_malloc64((*registry.native).szOsFile as u64).cast::<ffi::sqlite3_file>();
        if native.is_null() {
            return ffi::SQLITE_NOMEM;
        }
        ptr::write_bytes(native.cast::<u8>(), 0, (*registry.native).szOsFile as usize);
        let mut actual_flags = flags;
        let result = ((*registry.native).xOpen.unwrap())(
            registry.native,
            name,
            native,
            flags,
            &mut actual_flags,
        );
        if result != ffi::SQLITE_OK {
            if !(*native).pMethods.is_null() {
                ((*(*native).pMethods).xClose.unwrap())(native);
            }
            ffi::sqlite3_free(native.cast());
            return result;
        }
        let file =
            match registry.shared_file_with_create(&path, flags & ffi::SQLITE_OPEN_CREATE != 0) {
                Ok(file) => file,
                Err(_) => {
                    ((*(*native).pMethods).xClose.unwrap())(native);
                    ffi::sqlite3_free(native.cast());
                    return ffi::SQLITE_IOERR;
                }
            };
        Arc::increment_strong_count((*vfs).pAppData.cast::<EncryptedVfs>());
        let registration = Arc::from_raw((*vfs).pAppData.cast::<EncryptedVfs>());
        let file_state = Box::new(FileState {
            native,
            file,
            _registration: registration,
            readonly: actual_flags & ffi::SQLITE_OPEN_READONLY != 0,
        });
        if !out_flags.is_null() {
            *out_flags = actual_flags;
        }
        ptr::write(
            output.cast::<WrappedFile>(),
            WrappedFile {
                base: ffi::sqlite3_file { pMethods: &METHODS },
                state: Box::into_raw(file_state),
            },
        );
        ffi::SQLITE_OK
    })
}
unsafe extern "C" fn close(file: *mut ffi::sqlite3_file) -> c_int {
    boundary(|| unsafe {
        let outer = &mut *file.cast::<WrappedFile>();
        let owned = Box::from_raw(outer.state);
        outer.state = ptr::null_mut();
        outer.base.pMethods = ptr::null();
        ((*(*owned.native).pMethods).xClose.unwrap())(owned.native)
    })
}
unsafe extern "C" fn read(
    file: *mut ffi::sqlite3_file,
    buffer: *mut c_void,
    amount: c_int,
    offset: i64,
) -> c_int {
    boundary(|| unsafe {
        if amount < 0 || offset < 0 {
            return ffi::SQLITE_IOERR_READ;
        }
        let output = std::slice::from_raw_parts_mut(buffer.cast::<u8>(), amount as usize);
        output.fill(0); // SQLite requires the unread tail zeroed on short reads.
        let Ok(mut encrypted) = state(file).file.lock() else {
            return ffi::SQLITE_IOERR_READ;
        };
        let available = encrypted.size().saturating_sub(offset as u64).min(amount as u64) as usize;
        if available != 0
            && encrypted.read_exact_at(&mut output[..available], offset as u64).is_err()
        {
            output.fill(0);
            return ffi::SQLITE_IOERR_READ;
        }
        if available < amount as usize { ffi::SQLITE_IOERR_SHORT_READ } else { ffi::SQLITE_OK }
    })
}
unsafe extern "C" fn write(
    file: *mut ffi::sqlite3_file,
    buffer: *const c_void,
    amount: c_int,
    offset: i64,
) -> c_int {
    boundary(|| unsafe {
        if amount < 0 || offset < 0 {
            return ffi::SQLITE_IOERR_WRITE;
        }
        let s = state(file);
        if s.readonly {
            return ffi::SQLITE_READONLY;
        }
        let Ok(mut encrypted) = s.file.lock() else {
            return ffi::SQLITE_IOERR_WRITE;
        };
        status(
            encrypted.write_all_at(
                std::slice::from_raw_parts(buffer.cast::<u8>(), amount as usize),
                offset as u64,
            ),
            ffi::SQLITE_IOERR_WRITE,
        )
    })
}
unsafe extern "C" fn truncate(file: *mut ffi::sqlite3_file, size: i64) -> c_int {
    boundary(|| unsafe {
        if size < 0 {
            return ffi::SQLITE_IOERR_TRUNCATE;
        }
        let s = state(file);
        if s.readonly {
            return ffi::SQLITE_READONLY;
        }
        let Ok(mut encrypted) = s.file.lock() else {
            return ffi::SQLITE_IOERR_TRUNCATE;
        };
        status(encrypted.set_len(size as u64), ffi::SQLITE_IOERR_TRUNCATE)
    })
}
unsafe extern "C" fn sync(file: *mut ffi::sqlite3_file, flags: c_int) -> c_int {
    boundary(|| unsafe {
        let s = state(file);
        let Ok(mut encrypted) = s.file.lock() else {
            return ffi::SQLITE_IOERR_FSYNC;
        };
        if encrypted.sync().is_err() {
            return ffi::SQLITE_IOERR_FSYNC;
        }
        ((*(*s.native).pMethods).xSync.unwrap())(s.native, flags)
    })
}
unsafe extern "C" fn size(file: *mut ffi::sqlite3_file, output: *mut i64) -> c_int {
    boundary(|| unsafe {
        let Ok(encrypted) = state(file).file.lock() else {
            return ffi::SQLITE_IOERR_FSTAT;
        };
        let Ok(size) = i64::try_from(encrypted.size()) else {
            return ffi::SQLITE_IOERR_FSTAT;
        };
        *output = size;
        ffi::SQLITE_OK
    })
}

macro_rules! native_file_callback {
    ($name:ident, $method:ident, $fallback:expr $(, $arg:ident: $type:ty)*) => {
        unsafe extern "C" fn $name(file: *mut ffi::sqlite3_file $(, $arg: $type)*) -> c_int {
            boundary(|| unsafe {
                let s = state(file);
                (*(*s.native).pMethods).$method.map_or($fallback, |callback| callback(s.native $(, $arg)*))
            })
        }
    };
}
native_file_callback!(lock, xLock, ffi::SQLITE_IOERR_LOCK, level: c_int);
native_file_callback!(unlock, xUnlock, ffi::SQLITE_IOERR_UNLOCK, level: c_int);
native_file_callback!(reserved, xCheckReservedLock, ffi::SQLITE_IOERR_CHECKRESERVEDLOCK, output: *mut c_int);
native_file_callback!(shm_map, xShmMap, ffi::SQLITE_IOERR_SHMMAP, page: c_int, size: c_int, extend: c_int, output: *mut *mut c_void);
native_file_callback!(shm_lock, xShmLock, ffi::SQLITE_IOERR_SHMLOCK, offset: c_int, count: c_int, flags: c_int);
native_file_callback!(shm_unmap, xShmUnmap, ffi::SQLITE_IOERR_SHMMAP, delete: c_int);
unsafe extern "C" fn shm_barrier(file: *mut ffi::sqlite3_file) {
    let _ = boundary(|| unsafe {
        let s = state(file);
        if let Some(callback) = (*(*s.native).pMethods).xShmBarrier {
            callback(s.native);
        }
        ffi::SQLITE_OK
    });
}
unsafe extern "C" fn sector_size(_: *mut ffi::sqlite3_file) -> c_int {
    4096
}
unsafe extern "C" fn characteristics(_: *mut ffi::sqlite3_file) -> c_int {
    0
}
unsafe extern "C" fn fetch(
    _: *mut ffi::sqlite3_file,
    _: i64,
    _: c_int,
    output: *mut *mut c_void,
) -> c_int {
    unsafe {
        *output = ptr::null_mut();
    }
    ffi::SQLITE_OK
}
unsafe extern "C" fn unfetch(_: *mut ffi::sqlite3_file, _: i64, _: *mut c_void) -> c_int {
    ffi::SQLITE_OK
}
unsafe extern "C" fn control(
    file: *mut ffi::sqlite3_file,
    operation: c_int,
    argument: *mut c_void,
) -> c_int {
    boundary(|| unsafe {
        match operation {
            ffi::SQLITE_FCNTL_SIZE_HINT | ffi::SQLITE_FCNTL_CHUNK_SIZE => ffi::SQLITE_OK,
            ffi::SQLITE_FCNTL_MMAP_SIZE => {
                if !argument.is_null() {
                    *argument.cast::<i64>() = 0;
                }
                ffi::SQLITE_OK
            }
            // Only explicit metadata/locking controls can reach the empty native
            // stub. Unknown/new controls must not bypass encrypted data I/O.
            ffi::SQLITE_FCNTL_LOCKSTATE
            | ffi::SQLITE_FCNTL_LAST_ERRNO
            | ffi::SQLITE_FCNTL_PERSIST_WAL
            | ffi::SQLITE_FCNTL_HAS_MOVED
            | ffi::SQLITE_FCNTL_LOCK_TIMEOUT => {
                let s = state(file);
                (*(*s.native).pMethods).xFileControl.map_or(ffi::SQLITE_NOTFOUND, |callback| {
                    callback(s.native, operation, argument)
                })
            }
            _ => ffi::SQLITE_NOTFOUND,
        }
    })
}
static METHODS: ffi::sqlite3_io_methods = ffi::sqlite3_io_methods {
    iVersion: 3,
    xClose: Some(close),
    xRead: Some(read),
    xWrite: Some(write),
    xTruncate: Some(truncate),
    xSync: Some(sync),
    xFileSize: Some(size),
    xLock: Some(lock),
    xUnlock: Some(unlock),
    xCheckReservedLock: Some(reserved),
    xFileControl: Some(control),
    xSectorSize: Some(sector_size),
    xDeviceCharacteristics: Some(characteristics),
    xShmMap: Some(shm_map),
    xShmLock: Some(shm_lock),
    xShmBarrier: Some(shm_barrier),
    xShmUnmap: Some(shm_unmap),
    xFetch: Some(fetch),
    xUnfetch: Some(unfetch),
};

unsafe extern "C" fn delete(
    vfs: *mut ffi::sqlite3_vfs,
    name: *const c_char,
    sync_dir: c_int,
) -> c_int {
    boundary(|| unsafe {
        let registry = registration(vfs);
        let Some(path) = path(name) else {
            return ffi::SQLITE_IOERR_DELETE;
        };
        if registry.kind(&path).is_none() {
            return ffi::SQLITE_IOERR_DELETE;
        }
        let Ok(mut files) = registry.files.lock() else {
            return ffi::SQLITE_IOERR_DELETE;
        };
        if files.get(&path).and_then(Weak::upgrade).is_some() {
            return ffi::SQLITE_IOERR_DELETE;
        }
        if super::encrypted_file::delete(&path).is_err() {
            return ffi::SQLITE_IOERR_DELETE;
        }
        files.remove(&path);
        // Native xDelete also synchronizes the containing directory when asked,
        // making deletion of both the data companion and lock stub durable.
        ((*registry.native).xDelete.unwrap())(registry.native, name, sync_dir)
    })
}
unsafe extern "C" fn access(
    vfs: *mut ffi::sqlite3_vfs,
    name: *const c_char,
    flags: c_int,
    output: *mut c_int,
) -> c_int {
    boundary(|| unsafe {
        let registry = registration(vfs);
        let Some(path) = path(name) else {
            return ffi::SQLITE_IOERR_ACCESS;
        };
        if registry.kind(&path).is_none() {
            *output = 0;
            return ffi::SQLITE_OK;
        }
        if flags == ffi::SQLITE_ACCESS_EXISTS {
            *output = i32::from(backing(&path).exists());
            ffi::SQLITE_OK
        } else {
            ((*registry.native).xAccess.unwrap())(registry.native, name, flags, output)
        }
    })
}
macro_rules! native_vfs_callback {
    ($name:ident, $method:ident, $fallback:expr $(, $arg:ident: $type:ty)*) => {
        unsafe extern "C" fn $name(vfs: *mut ffi::sqlite3_vfs $(, $arg: $type)*) -> c_int {
            boundary(|| unsafe { let native = registration(vfs).native; (*native).$method.map_or($fallback, |callback| callback(native $(, $arg)*)) })
        }
    };
}
native_vfs_callback!(full_pathname, xFullPathname, ffi::SQLITE_CANTOPEN, name: *const c_char, length: c_int, output: *mut c_char);
native_vfs_callback!(randomness, xRandomness, 0, length: c_int, output: *mut c_char);
native_vfs_callback!(sleep, xSleep, 0, micros: c_int);
native_vfs_callback!(current_time, xCurrentTime, ffi::SQLITE_ERROR, output: *mut f64);
native_vfs_callback!(last_error, xGetLastError, 0, length: c_int, output: *mut c_char);
native_vfs_callback!(current_time_int64, xCurrentTimeInt64, ffi::SQLITE_ERROR, output: *mut i64);

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{
        encryption::{
            DatabaseKey, DatabaseKeyLease, DatabaseKeyVersion, EncryptionError, KeyPurpose,
            KeyScope, KeyVersionMetadata,
        },
        identity::{DatabaseUlid, ProjectUlid, TenantUlid},
    };
    use rusqlite::{Connection, OpenFlags};
    use std::time::Duration;

    #[derive(Debug)]
    struct Keys(Arc<DatabaseKeyLease>);
    impl DatabaseKeySource for Keys {
        fn current_lease(&self) -> Result<Arc<DatabaseKeyLease>, EncryptionError> {
            Ok(self.0.clone())
        }
        fn lease(
            &self,
            version: DatabaseKeyVersion,
        ) -> Result<Arc<DatabaseKeyLease>, EncryptionError> {
            if version == self.0.metadata().version() {
                Ok(self.0.clone())
            } else {
                Err(EncryptionError::InvalidKeyMetadata)
            }
        }
    }
    fn keys(byte: u8) -> Arc<dyn DatabaseKeySource> {
        let scope = KeyScope::new_with_project(
            "production",
            TenantUlid::from_text("01ARZ3NDEKTSV4RRFFQ69G5FAV").unwrap(),
            ProjectUlid::from_text("01ARZ3NDEKTSV4RRFFQ69G5FAW").unwrap(),
            DatabaseUlid::from_text("01ARZ3NDEKTSV4RRFFQ69G5FAX").unwrap(),
            KeyPurpose::Data,
        )
        .unwrap();
        let metadata = KeyVersionMetadata::new(
            scope,
            DatabaseKeyVersion::from_text("01ARZ3NDEKTSV4RRFFQ69G5FB0").unwrap(),
        );
        Arc::new(Keys(Arc::new(
            DatabaseKeyLease::test_oci_vault(
                metadata,
                DatabaseKey::from_bytes([byte; 32]),
                Duration::from_secs(300),
            )
            .unwrap(),
        )))
    }
    fn generation(path: &Path) -> PathBuf {
        let manifest = std::fs::read(backing(path)).unwrap();
        let mut generation = backing(path).into_os_string();
        generation.push(".gen-");
        generation
            .push(manifest[8..40].iter().map(|byte| format!("{byte:02x}")).collect::<String>());
        PathBuf::from(generation)
    }

    struct Directory(PathBuf);
    impl Directory {
        fn new() -> Self {
            let path = std::env::temp_dir().join(format!(
                "terrabase-encrypted-vfs-{}-{}",
                std::process::id(),
                NEXT_ID.fetch_add(1, Ordering::Relaxed)
            ));
            std::fs::create_dir_all(&path).unwrap();
            Self(path)
        }
        fn database(&self) -> PathBuf {
            self.0.join("data.sqlite3")
        }
    }
    impl Drop for Directory {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }
    fn connect(path: &Path, vfs: &EncryptedVfs) -> Connection {
        let connection = Connection::open_with_flags_and_vfs(
            path,
            OpenFlags::SQLITE_OPEN_READ_WRITE | OpenFlags::SQLITE_OPEN_CREATE,
            vfs.name(),
        )
        .unwrap();
        connection
            .execute_batch(
                "PRAGMA temp_store=MEMORY; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;",
            )
            .unwrap();
        connection
    }

    #[test]
    fn wal_checkpoint_reopen_keeps_native_stubs_empty_and_payload_encrypted() {
        let directory = Directory::new();
        let path = directory.database();
        let vfs = EncryptedVfs::register(&path, keys(17)).unwrap();
        let connection = connect(&path, &vfs);
        connection.execute_batch("PRAGMA mmap_size=1073741824; CREATE TABLE secrets(id INTEGER PRIMARY KEY, value TEXT);").unwrap();
        let secret = "private-record-must-never-appear-in-native-sqlite-files".repeat(200);
        connection.execute("INSERT INTO secrets(value) VALUES (?1)", [&secret]).unwrap();
        let reader = connect(&path, &vfs);
        assert_eq!(
            reader.query_row("SELECT value FROM secrets", [], |r| r.get::<_, String>(0)).unwrap(),
            secret
        );
        for entry in std::fs::read_dir(&directory.0).unwrap() {
            let bytes = std::fs::read(entry.unwrap().path()).unwrap();
            assert!(!bytes.windows(40).any(|window| window == &secret.as_bytes()[..40]));
        }
        assert_eq!(std::fs::metadata(&path).unwrap().len(), 0);
        connection.execute_batch("PRAGMA wal_checkpoint(TRUNCATE);").unwrap();
        drop(reader);
        drop(connection);
        drop(vfs);
        let reopened = EncryptedVfs::register(&path, keys(17)).unwrap();
        let connection = connect(&path, &reopened);
        assert_eq!(
            connection.query_row("PRAGMA integrity_check", [], |r| r.get::<_, String>(0)).unwrap(),
            "ok"
        );
        assert_eq!(
            connection
                .query_row("SELECT value FROM secrets", [], |r| r.get::<_, String>(0))
                .unwrap(),
            secret
        );
    }

    #[test]
    fn connection_retains_registration_and_native_writer_lock_across_threads() {
        let directory = Directory::new();
        let path = directory.database();
        let vfs = EncryptedVfs::register(&path, keys(18)).unwrap();
        let connection = connect(&path, &vfs);
        connection.execute_batch("CREATE TABLE values_table(value INTEGER); INSERT INTO values_table VALUES(1); BEGIN IMMEDIATE;").unwrap();
        let reader = connect(&path, &vfs);
        let name = CString::new(vfs.name()).unwrap();
        drop(vfs);
        let reader = std::thread::spawn(move || {
            reader.busy_timeout(Duration::ZERO).unwrap();
            assert_eq!(
                reader
                    .query_row("SELECT value FROM values_table", [], |r| r.get::<_, i64>(0))
                    .unwrap(),
                1
            );
            let error = reader.execute_batch("BEGIN IMMEDIATE").unwrap_err();
            assert_eq!(error.sqlite_error_code(), Some(rusqlite::ErrorCode::DatabaseBusy));
            reader
        })
        .join()
        .unwrap();
        connection.execute_batch("UPDATE values_table SET value=2; COMMIT;").unwrap();
        assert_eq!(
            reader.query_row("SELECT value FROM values_table", [], |r| r.get::<_, i64>(0)).unwrap(),
            2
        );
        assert!(!unsafe { ffi::sqlite3_vfs_find(name.as_ptr()) }.is_null());
        drop(reader);
        drop(connection);
        assert!(unsafe { ffi::sqlite3_vfs_find(name.as_ptr()) }.is_null());
    }

    #[test]
    fn authenticated_data_failure_propagates_to_sqlite_without_overwriting_ciphertext() {
        use std::io::{Read, Seek, SeekFrom, Write};
        let directory = Directory::new();
        let path = directory.database();
        let vfs = EncryptedVfs::register(&path, keys(24)).unwrap();
        let connection = connect(&path, &vfs);
        connection
            .execute_batch(
                "CREATE TABLE secrets(value TEXT); INSERT INTO secrets VALUES('protected');",
            )
            .unwrap();
        drop(connection);
        drop(vfs);
        let mut file =
            std::fs::OpenOptions::new().read(true).write(true).open(backing(&path)).unwrap();
        file.seek(SeekFrom::Start(40 + 100)).unwrap();
        let mut byte = [0];
        file.read_exact(&mut byte).unwrap();
        byte[0] ^= 1;
        file.seek(SeekFrom::Start(40 + 100)).unwrap();
        file.write_all(&byte).unwrap();
        file.sync_all().unwrap();
        let corrupted = std::fs::read(backing(&path)).unwrap();
        let vfs = EncryptedVfs::register(&path, keys(24)).unwrap();
        let error = Connection::open_with_flags_and_vfs(
            &path,
            OpenFlags::SQLITE_OPEN_READ_WRITE,
            vfs.name(),
        )
        .unwrap_err();
        assert_eq!(error.sqlite_error_code(), Some(rusqlite::ErrorCode::SystemIoFailure));
        assert_eq!(std::fs::read(backing(&path)).unwrap(), corrupted);
    }

    #[test]
    fn wrong_key_and_out_of_scope_attach_fail_closed() {
        let directory = Directory::new();
        let path = directory.database();
        let vfs = EncryptedVfs::register(&path, keys(19)).unwrap();
        let connection = connect(&path, &vfs);
        connection
            .execute_batch(
                "CREATE TABLE secrets(value TEXT); INSERT INTO secrets VALUES('hidden');",
            )
            .unwrap();
        let outside = directory.0.join("outside.sqlite3");
        assert!(
            connection
                .execute("ATTACH DATABASE ?1 AS outside", [outside.to_str().unwrap()])
                .is_err()
        );
        assert!(!outside.exists());
        drop(connection);
        drop(vfs);
        let wrong = EncryptedVfs::register(&path, keys(20)).unwrap();
        assert!(
            Connection::open_with_flags_and_vfs(
                &path,
                OpenFlags::SQLITE_OPEN_READ_WRITE,
                wrong.name()
            )
            .is_err()
        );
        drop(wrong);
        let correct = EncryptedVfs::register(&path, keys(19)).unwrap();
        let connection = connect(&path, &correct);
        assert_eq!(
            connection
                .query_row("SELECT value FROM secrets", [], |r| r.get::<_, String>(0))
                .unwrap(),
            "hidden"
        );
    }

    #[test]
    fn registration_reuses_same_authority_and_rejects_concurrent_rekey() {
        let directory = Directory::new();
        let path = directory.database();
        let authority = keys(23);
        let first = EncryptedVfs::register(&path, authority.clone()).unwrap();
        let second = EncryptedVfs::register(&path, authority).unwrap();
        assert!(Arc::ptr_eq(&first, &second));
        assert!(EncryptedVfs::register(&path, keys(23)).is_err());
        assert!(Arc::ptr_eq(&first, &lookup(&path).unwrap()));
        drop(first);
        drop(second);
        assert!(lookup(&path).is_none());
        assert!(EncryptedVfs::register(&path, keys(23)).is_ok());
    }

    #[test]
    fn missing_encrypted_companion_never_reinitializes_existing_database() {
        let directory = Directory::new();
        let path = directory.database();
        let vfs = EncryptedVfs::register(&path, keys(22)).unwrap();
        let connection = connect(&path, &vfs);
        connection
            .execute_batch("CREATE TABLE retained(value INTEGER); INSERT INTO retained VALUES(42);")
            .unwrap();
        drop(connection);
        std::fs::remove_file(backing(&path)).unwrap();
        assert!(
            Connection::open_with_flags_and_vfs(
                &path,
                OpenFlags::SQLITE_OPEN_READ_WRITE | OpenFlags::SQLITE_OPEN_CREATE,
                vfs.name()
            )
            .is_err()
        );
        assert!(!backing(&path).exists());
    }

    #[test]
    fn leftover_auxiliary_stub_recovers_after_interrupted_delete() {
        let directory = Directory::new();
        let path = directory.database();
        let vfs = EncryptedVfs::register(&path, keys(28)).unwrap();
        let connection = connect(&path, &vfs);
        connection
            .execute_batch("CREATE TABLE retained(value INTEGER); INSERT INTO retained VALUES(1);")
            .unwrap();
        drop(connection);
        let wal = path.with_file_name("data.sqlite3-wal");
        std::fs::write(&wal, []).unwrap();
        assert!(!backing(&wal).exists());
        let connection = connect(&path, &vfs);
        connection.execute_batch("INSERT INTO retained VALUES(2);").unwrap();
        assert_eq!(
            connection
                .query_row("SELECT COUNT(*) FROM retained", [], |row| row.get::<_, i64>(0))
                .unwrap(),
            2
        );
    }

    #[test]
    fn existing_encrypted_open_never_recreates_missing_backing() {
        let directory = Directory::new();
        let path = directory.database();
        assert!(EncryptedFile::open_existing(&path, keys(25), ArtifactKind::Database).is_err());
        assert_eq!(std::fs::read_dir(&directory.0).unwrap().count(), 0);

        let mut file = EncryptedFile::open(&path, keys(25), ArtifactKind::Database).unwrap();
        file.write_all_at(&[42; 4096], 0).unwrap();
        file.sync().unwrap();
        drop(file);
        let mut file =
            EncryptedFile::open_existing(&path, keys(25), ArtifactKind::Database).unwrap();
        let mut retained_bytes = [0; 4096];
        file.read_exact_at(&mut retained_bytes, 0).unwrap();
        assert_eq!(retained_bytes, [42; 4096]);
        drop(file);

        std::fs::remove_file(backing(&path)).unwrap();
        let retained: std::collections::BTreeSet<_> = std::fs::read_dir(&directory.0)
            .unwrap()
            .map(|entry| entry.unwrap().file_name())
            .collect();
        assert!(EncryptedFile::open_existing(&path, keys(25), ArtifactKind::Database).is_err());
        assert!(!backing(&path).exists());
        assert_eq!(
            retained,
            std::fs::read_dir(&directory.0)
                .unwrap()
                .map(|entry| entry.unwrap().file_name())
                .collect()
        );
    }

    #[test]
    fn encrypted_records_recover_partial_tail_and_compact_overwrites() {
        use std::io::Write;
        let directory = Directory::new();
        let path = directory.database();
        let mut file = EncryptedFile::open(&path, keys(25), ArtifactKind::Database).unwrap();
        for value in 0..150_u8 {
            file.write_all_at(&[value; 4096], 0).unwrap();
        }
        file.sync().unwrap();
        assert!(std::fs::metadata(generation(&path)).unwrap().len() < 600_000);
        file.set_len(17).unwrap();
        file.set_len(4096).unwrap();
        file.sync().unwrap();
        drop(file);
        let generation = generation(&path);
        let durable_length = std::fs::metadata(&generation).unwrap().len();
        std::fs::OpenOptions::new()
            .append(true)
            .open(&generation)
            .unwrap()
            .write_all(&[77; 8192 + 123])
            .unwrap();
        let mut file = EncryptedFile::open(&path, keys(25), ArtifactKind::Database).unwrap();
        assert_eq!(std::fs::metadata(&generation).unwrap().len(), durable_length);
        let mut bytes = [0; 4096];
        file.read_exact_at(&mut bytes, 0).unwrap();
        assert_eq!(&bytes[..17], &[149; 17]);
        assert!(bytes[17..].iter().all(|byte| *byte == 0));
    }

    #[test]
    fn durable_generation_rejects_corruption_and_old_generation_records() {
        use std::io::{Seek, SeekFrom, Write};
        let directory = Directory::new();
        let path = directory.database();
        let mut file = EncryptedFile::open(&path, keys(29), ArtifactKind::Database).unwrap();
        file.write_all_at(&[1; 4096], 0).unwrap();
        file.sync().unwrap();
        let original_generation = generation(&path);
        let old_bytes = std::fs::read(&original_generation).unwrap();
        for byte in 2..100_u8 {
            file.write_all_at(&[byte; 4096], 0).unwrap();
        }
        file.sync().unwrap();
        drop(file);
        let selected = generation(&path);
        assert_ne!(selected, original_generation);
        let good = std::fs::read(&selected).unwrap();
        let mut disk = std::fs::OpenOptions::new().write(true).open(&selected).unwrap();
        // Replay a complete, previously valid record at the same ordinal from
        // the generation retired by compaction. Authentication must reject it.
        disk.seek(SeekFrom::Start(4096 + 8192)).unwrap();
        disk.write_all(&old_bytes[4096 + 8192..4096 + 2 * 8192]).unwrap();
        disk.sync_all().unwrap();
        assert!(EncryptedFile::open(&path, keys(29), ArtifactKind::Database).is_err());
        disk.seek(SeekFrom::Start(0)).unwrap();
        disk.write_all(&good).unwrap();
        disk.seek(SeekFrom::Start(4096 + 100)).unwrap();
        disk.write_all(&[good[4096 + 100] ^ 1]).unwrap();
        disk.sync_all().unwrap();
        assert!(EncryptedFile::open(&path, keys(29), ArtifactKind::Database).is_err());
    }

    #[test]
    fn compaction_publication_failures_reopen_a_complete_generation() {
        for stage in [
            "generation_synced",
            "generation_directory_synced",
            "manifest_written",
            "manifest_synced",
            "manifest_renamed",
            "manifest_directory_synced",
        ] {
            let directory = Directory::new();
            let path = directory.database();
            let mut file = EncryptedFile::open(&path, keys(31), ArtifactKind::Database).unwrap();
            file.write_all_at(b"durable payload", 0).unwrap();
            file.sync().unwrap();
            let before = generation(&path);
            super::super::encrypted_file::fail_next_publication_at(stage);
            assert!(file.reencrypt().is_err(), "{stage}");
            drop(file);
            let selected = generation(&path);
            if matches!(stage, "manifest_renamed" | "manifest_directory_synced") {
                assert_ne!(before, selected);
            } else {
                assert_eq!(before, selected);
            }
            let mut file = EncryptedFile::open(&path, keys(31), ArtifactKind::Database).unwrap();
            let mut bytes = [0; 15];
            file.read_exact_at(&mut bytes, 0).unwrap();
            assert_eq!(&bytes, b"durable payload", "{stage}");
            assert!(selected.exists(), "{stage}");
        }
    }

    #[test]
    fn tag_corruption_is_discardable_only_after_durable_frontier() {
        use std::io::{Seek, SeekFrom, Write};
        for durable in [false, true] {
            let directory = Directory::new();
            let path = directory.database();
            let mut file = EncryptedFile::open(&path, keys(32), ArtifactKind::Database).unwrap();
            file.write_all_at(b"old", 0).unwrap();
            file.sync().unwrap();
            file.write_all_at(b"new", 0).unwrap();
            if durable {
                file.sync().unwrap();
            }
            drop(file);
            let selected = generation(&path);
            let mut bytes = std::fs::read(&selected).unwrap();
            let record = bytes.len() - 8192;
            let sealed = u32::from_le_bytes(bytes[record..record + 4].try_into().unwrap()) as usize;
            let tag = record + 66 + sealed - 1;
            bytes[tag] ^= 1;
            let mut disk = std::fs::OpenOptions::new().write(true).open(&selected).unwrap();
            disk.seek(SeekFrom::Start(tag as u64)).unwrap();
            disk.write_all(&bytes[tag..tag + 1]).unwrap();
            disk.sync_all().unwrap();
            let reopened = EncryptedFile::open(&path, keys(32), ArtifactKind::Database);
            if durable {
                assert!(reopened.is_err());
            } else {
                let mut reopened = reopened.unwrap();
                let mut output = [0; 3];
                reopened.read_exact_at(&mut output, 0).unwrap();
                assert_eq!(&output, b"old");
            }
        }
    }

    #[test]
    fn reencrypt_retires_old_key_without_losing_live_blocks() {
        use std::sync::atomic::AtomicBool;
        #[derive(Debug)]
        struct Rotating {
            old: Arc<DatabaseKeyLease>,
            new: Arc<DatabaseKeyLease>,
            switched: AtomicBool,
            retired: AtomicBool,
        }
        impl DatabaseKeySource for Rotating {
            fn current_lease(&self) -> Result<Arc<DatabaseKeyLease>, EncryptionError> {
                Ok(if self.switched.load(Ordering::SeqCst) {
                    self.new.clone()
                } else {
                    self.old.clone()
                })
            }
            fn lease(
                &self,
                version: DatabaseKeyVersion,
            ) -> Result<Arc<DatabaseKeyLease>, EncryptionError> {
                if version == self.new.metadata().version() {
                    Ok(self.new.clone())
                } else if version == self.old.metadata().version()
                    && !self.retired.load(Ordering::SeqCst)
                {
                    Ok(self.old.clone())
                } else {
                    Err(EncryptionError::InvalidKeyMetadata)
                }
            }
        }
        let old = keys(33).current_lease().unwrap();
        let metadata = KeyVersionMetadata::new(
            old.metadata().scope().clone(),
            DatabaseKeyVersion::from_text("01ARZ3NDEKTSV4RRFFQ69G5FB1").unwrap(),
        );
        let new = Arc::new(
            DatabaseKeyLease::test_oci_vault(
                metadata,
                DatabaseKey::from_bytes([34; 32]),
                Duration::from_secs(300),
            )
            .unwrap(),
        );
        let keys = Arc::new(Rotating {
            old,
            new,
            switched: AtomicBool::new(false),
            retired: AtomicBool::new(false),
        });
        let directory = Directory::new();
        let path = directory.database();
        let mut file = EncryptedFile::open(&path, keys.clone(), ArtifactKind::Database).unwrap();
        file.write_all_at(&[91; 9000], 0).unwrap();
        file.sync().unwrap();
        keys.switched.store(true, Ordering::SeqCst);
        file.reencrypt().unwrap();
        keys.retired.store(true, Ordering::SeqCst);
        let mut bytes = [0; 9000];
        file.read_exact_at(&mut bytes, 0).unwrap();
        assert_eq!(bytes, [91; 9000]);
        drop(file);
        let mut file = EncryptedFile::open(&path, keys, ArtifactKind::Database).unwrap();
        file.read_exact_at(&mut bytes, 0).unwrap();
        assert_eq!(bytes, [91; 9000]);
    }

    #[test]
    fn valid_unsynced_encrypted_suffix_survives_reopen() {
        let directory = Directory::new();
        let path = directory.database();
        let mut file = EncryptedFile::open(&path, keys(30), ArtifactKind::Database).unwrap();
        file.write_all_at(b"synced", 0).unwrap();
        file.sync().unwrap();
        file.write_all_at(b"written", 0).unwrap();
        drop(file);
        let mut file = EncryptedFile::open(&path, keys(30), ArtifactKind::Database).unwrap();
        let mut bytes = [0; 7];
        file.read_exact_at(&mut bytes, 0).unwrap();
        assert_eq!(&bytes, b"written");
    }

    #[test]
    fn encrypted_file_lock_excludes_independent_handles_and_role_substitution() {
        let directory = Directory::new();
        let path = directory.database();
        let mut file = EncryptedFile::open(&path, keys(26), ArtifactKind::Database).unwrap();
        file.write_all_at(b"database secret", 0).unwrap();
        file.sync().unwrap();
        assert!(EncryptedFile::open(&path, keys(26), ArtifactKind::Database).is_err());
        assert!(super::super::encrypted_file::delete(&path).is_err());
        assert!(backing(&path).exists());
        drop(file);
        let wal = path.with_file_name("data.sqlite3-wal");
        std::fs::copy(backing(&path), backing(&wal)).unwrap();
        assert!(EncryptedFile::open(&wal, keys(26), ArtifactKind::Wal).is_err());
        std::fs::remove_file(backing(&wal)).unwrap();
        let mut file = EncryptedFile::open(&wal, keys(26), ArtifactKind::Wal).unwrap();
        file.write_all_at(b"wal secret", 0).unwrap();
        file.sync().unwrap();
        drop(file);
        let journal = path.with_file_name("data.sqlite3-journal");
        std::fs::copy(backing(&wal), backing(&journal)).unwrap();
        assert!(EncryptedFile::open(&journal, keys(26), ArtifactKind::Wal).is_err());
    }

    #[test]
    fn crash_child_leaves_committed_wal() {
        let Some(path) = std::env::var_os("TERRABASE_ENCRYPTED_VFS_CRASH_PATH") else {
            return;
        };
        let path = PathBuf::from(path);
        let vfs = EncryptedVfs::register(&path, keys(27)).unwrap();
        let connection = connect(&path, &vfs);
        connection.execute_batch("PRAGMA wal_autocheckpoint=0; CREATE TABLE crash_recovery(value TEXT); INSERT INTO crash_recovery VALUES('committed before exit');").unwrap();
        // Immediate process termination skips Connection/guard destructors and
        // leaves the committed transaction exclusively in the encrypted WAL.
        std::process::exit(0);
    }

    #[test]
    fn committed_encrypted_wal_recovers_after_process_exit_without_close() {
        let directory = Directory::new();
        let path = directory.database();
        let status = std::process::Command::new(std::env::current_exe().unwrap())
            .arg("storage::sqlite::encrypted_vfs::tests::crash_child_leaves_committed_wal")
            .arg("--exact")
            .arg("--nocapture")
            .env("TERRABASE_ENCRYPTED_VFS_CRASH_PATH", &path)
            .status()
            .unwrap();
        assert!(status.success());
        assert!(backing(&path.with_file_name("data.sqlite3-wal")).exists());
        let vfs = EncryptedVfs::register(&path, keys(27)).unwrap();
        let connection = connect(&path, &vfs);
        assert_eq!(
            connection
                .query_row("SELECT value FROM crash_recovery", [], |row| row.get::<_, String>(0))
                .unwrap(),
            "committed before exit"
        );
        assert_eq!(
            connection
                .query_row("PRAGMA integrity_check", [], |row| row.get::<_, String>(0))
                .unwrap(),
            "ok"
        );
    }

    #[test]
    fn sqlite_short_reads_zero_fill_and_readonly_writes_are_rejected() {
        let directory = Directory::new();
        let path = directory.database();
        let vfs = EncryptedVfs::register(&path, keys(21)).unwrap();
        let name = CString::new(path.to_str().unwrap()).unwrap();
        let mut file = WrappedFile {
            base: ffi::sqlite3_file { pMethods: ptr::null() },
            state: ptr::null_mut(),
        };
        let flags = ffi::SQLITE_OPEN_READWRITE | ffi::SQLITE_OPEN_CREATE | ffi::SQLITE_OPEN_MAIN_DB;
        unsafe {
            assert_eq!(
                open(vfs.vfs.get(), name.as_ptr(), &mut file.base, flags, ptr::null_mut()),
                ffi::SQLITE_OK
            );
            assert_eq!(write(&mut file.base, b"abc".as_ptr().cast(), 3, 0), ffi::SQLITE_OK);
            let mut output = [99_u8; 6];
            assert_eq!(
                read(&mut file.base, output.as_mut_ptr().cast(), 6, 1),
                ffi::SQLITE_IOERR_SHORT_READ
            );
            assert_eq!(output, [b'b', b'c', 0, 0, 0, 0]);
            (*file.state).readonly = true;
            assert_eq!(write(&mut file.base, b"bad".as_ptr().cast(), 3, 0), ffi::SQLITE_READONLY);
            assert_eq!(truncate(&mut file.base, 0), ffi::SQLITE_READONLY);
            assert_eq!(close(&mut file.base), ffi::SQLITE_OK);
            assert!(file.base.pMethods.is_null());
        }
    }
}
