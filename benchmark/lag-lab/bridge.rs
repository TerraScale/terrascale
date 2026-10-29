#[no_mangle]
pub unsafe extern "C" fn ts_sync_new(base: *const std::ffi::c_char, cap: usize, mvcc: bool) -> *mut TursoSyncServer {
    let base = std::ffi::CStr::from_ptr(base).to_string_lossy();
    let opts = DatabaseOpts::default().with_experimental_mvcc_passive_checkpoint(mvcc);
    match TursoSyncServer::new_dir(String::new(), PathBuf::from(base.as_ref()), Arc::new(AtomicUsize::new(0)), OpenConfig { vfs: None, flags: OpenFlags::default(), db_opts: opts, max_open: cap }) {
        Ok(s) => Box::into_raw(Box::new(s)), Err(_) => std::ptr::null_mut()
    }
}
#[no_mangle]
pub unsafe extern "C" fn ts_sync_request(host: *mut TursoSyncServer, name: *const std::ffi::c_char, pipeline: bool, req: *const u8, len: usize, out: *mut *mut u8, outlen: *mut usize) -> u16 {
    let host = &*host;
    let name = std::ffi::CStr::from_ptr(name).to_string_lossy();
    let body = std::slice::from_raw_parts(req, len);
    let resp = match host.resolve_db(Some(&name)) {
        Err(e) => e,
        Ok(db) => match if pipeline {host.handle_pipeline(&db, body)} else {host.handle_pull_updates(&db, body)} {
            Ok(r) => r, Err(e) => text_response(500, &e.to_string())
        }
    };
    let boxed = resp.body.into_boxed_slice();
    *outlen = boxed.len(); *out = Box::into_raw(boxed) as *mut u8;
    resp.status
}
#[no_mangle]
pub unsafe extern "C" fn ts_sync_free(ptr: *mut u8, len: usize) { drop(Box::from_raw(std::ptr::slice_from_raw_parts_mut(ptr,len))); }
#[no_mangle]
pub unsafe extern "C" fn ts_sync_drop(host: *mut TursoSyncServer) { drop(Box::from_raw(host)); }
