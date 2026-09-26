//! Minimal flat C ABI over a Loro document for the TerraBase comparison bench.
//!
//! Handles are raw pointers boxed into `usize`; every string boundary is
//! validated as UTF-8 before use.

use loro::{ExportMode, LoroDoc, LoroValue, Subscription, ToJson};
use std::ffi::{c_char, CStr};
use std::sync::atomic::{AtomicI64, Ordering};
use std::sync::{Arc, OnceLock};
use std::time::Instant;

struct Doc {
    doc: LoroDoc,
    events: Arc<AtomicI64>,
    last_event_ns: AtomicI64,
    sub: Option<Subscription>,
}

fn epoch() -> &'static Instant {
    static EPOCH: OnceLock<Instant> = OnceLock::new();
    EPOCH.get_or_init(Instant::now)
}

fn now_ns() -> i64 {
    i64::try_from(epoch().elapsed().as_nanos()).unwrap_or(i64::MAX)
}

fn take(h: usize) -> Option<&'static mut Doc> {
    unsafe { (h as *mut Doc).as_mut() }
}

fn cstr<'a>(ptr: *const c_char) -> Result<&'a str, i32> {
    if ptr.is_null() {
        return Err(-1);
    }
    unsafe { CStr::from_ptr(ptr) }.to_str().map_err(|_| -1)
}

fn to_loro(value: serde_json::Value) -> LoroValue {
    match value {
        serde_json::Value::Null => LoroValue::Null,
        serde_json::Value::Bool(b) => LoroValue::Bool(b),
        serde_json::Value::Number(n) => n
            .as_i64()
            .map(LoroValue::I64)
            .unwrap_or_else(|| LoroValue::Double(n.as_f64().unwrap_or_default())),
        serde_json::Value::String(s) => LoroValue::String(s.into()),
        serde_json::Value::Array(a) => LoroValue::List(a.into_iter().map(to_loro).collect()),
        serde_json::Value::Object(o) => {
            LoroValue::Map(o.into_iter().map(|(k, v)| (k, to_loro(v))).collect())
        }
    }
}

/// Copies `bytes` into `out` (capacity `cap`). With `cap == 0` returns the
/// needed size without writing, so callers can size, allocate, then re-call.
fn write_out(out: *mut u8, cap: usize, bytes: &[u8]) -> i64 {
    let len = i64::try_from(bytes.len()).unwrap_or(i64::MAX);
    if cap == 0 || out.is_null() {
        return len;
    }
    if bytes.len() > cap {
        return -1;
    }
    unsafe { std::ptr::copy_nonoverlapping(bytes.as_ptr(), out, bytes.len()) };
    len
}

#[no_mangle]
pub extern "C" fn lob_new() -> usize {
    let doc = LoroDoc::new();
    doc.set_record_timestamp(false);
    Box::into_raw(Box::new(Doc {
        doc,
        events: Arc::new(AtomicI64::new(0)),
        last_event_ns: AtomicI64::new(0),
        sub: None,
    })) as usize
}

#[no_mangle]
pub extern "C" fn lob_free(handle: usize) {
    drop(unsafe { Box::from_raw(handle as *mut Doc) });
}

/// Sets `container[key] = json_value` where `json_value` is JSON-encoded.
#[no_mangle]
pub extern "C" fn lob_map_set(
    handle: usize,
    container: *const c_char,
    key: *const c_char,
    json_value: *const c_char,
) -> i32 {
    let Some(doc) = take(handle) else { return -1 };
    let (Ok(container), Ok(key), Ok(json)) = (cstr(container), cstr(key), cstr(json_value)) else {
        return -1;
    };
    let Ok(value) = serde_json::from_str::<serde_json::Value>(json) else {
        return -1;
    };
    doc.doc
        .get_map(container)
        .insert(key, to_loro(value))
        .map(|_| 0)
        .unwrap_or(-1)
}

#[no_mangle]
pub extern "C" fn lob_commit(handle: usize) -> i32 {
    let Some(doc) = take(handle) else { return -1 };
    doc.doc.commit();
    0
}

#[no_mangle]
pub extern "C" fn lob_export_snapshot(handle: usize, out: *mut u8, cap: usize) -> i64 {
    let Some(doc) = take(handle) else { return -1 };
    match doc.doc.export(ExportMode::Snapshot) {
        Ok(bytes) => write_out(out, cap, &bytes),
        Err(_) => -1,
    }
}

/// Exports every update since document creation (the doc starts empty, so
/// `all_updates` is exactly "updates since start").
#[no_mangle]
pub extern "C" fn lob_export_updates(handle: usize, out: *mut u8, cap: usize) -> i64 {
    let Some(doc) = take(handle) else { return -1 };
    match doc.doc.export(ExportMode::all_updates()) {
        Ok(bytes) => write_out(out, cap, &bytes),
        Err(_) => -1,
    }
}

#[no_mangle]
pub extern "C" fn lob_import(handle: usize, bytes: *const u8, len: usize) -> i32 {
    let Some(doc) = take(handle) else { return -1 };
    if bytes.is_null() && len != 0 {
        return -1;
    }
    let slice = unsafe { std::slice::from_raw_parts(bytes, len) };
    doc.doc.import(slice).map(|_| 0).unwrap_or(-1)
}

/// Subscribes to root events; each fired event bumps a counter and records
/// the shim-clock nanosecond timestamp of the callback.
#[no_mangle]
pub extern "C" fn lob_subscribe_root(handle: usize) -> i32 {
    let Some(doc) = take(handle) else { return -1 };
    let events = Arc::clone(&doc.events);
    let last: &AtomicI64 = &doc.last_event_ns;
    let sub = doc.doc.subscribe_root(Arc::new(move |_| {
        events.fetch_add(1, Ordering::Relaxed);
        last.store(now_ns(), Ordering::Relaxed);
    }));
    doc.sub = Some(sub);
    0
}

#[no_mangle]
pub extern "C" fn lob_last_event_count(handle: usize) -> i64 {
    let Some(doc) = take(handle) else { return -1 };
    doc.events.load(Ordering::Relaxed)
}

#[no_mangle]
pub extern "C" fn lob_reset_event_stats(handle: usize) {
    if let Some(doc) = take(handle) {
        doc.events.store(0, Ordering::Relaxed);
        doc.last_event_ns.store(0, Ordering::Relaxed);
    }
}

/// Nanoseconds on the shim's monotonic clock at the last event callback.
#[no_mangle]
pub extern "C" fn lob_last_event_ns(handle: usize) -> i64 {
    let Some(doc) = take(handle) else { return -1 };
    doc.last_event_ns.load(Ordering::Relaxed)
}

/// Current shim-clock nanoseconds; C# aligns clocks by sampling this and
/// `Stopwatch.GetTimestamp()` at the same instant.
#[no_mangle]
pub extern "C" fn lob_now_ns() -> i64 {
    now_ns()
}

/// Number of ops retained by the document oplog.
#[no_mangle]
pub extern "C" fn lob_doc_size_ops(handle: usize) -> i64 {
    let Some(doc) = take(handle) else { return -1 };
    i64::try_from(doc.doc.len_ops()).unwrap_or(i64::MAX)
}

/// Reads `container[key]` as JSON. Returns the needed size (cap=0 pattern),
/// -1 on error, -2 when the key is absent.
#[no_mangle]
pub extern "C" fn lob_read_entry(
    handle: usize,
    container: *const c_char,
    key: *const c_char,
    out: *mut u8,
    cap: usize,
) -> i64 {
    let Some(doc) = take(handle) else { return -1 };
    let (Ok(container), Ok(key)) = (cstr(container), cstr(key)) else {
        return -1;
    };
    let Some(map) = doc.doc.try_get_map(container) else {
        return -2;
    };
    let Some(value) = map.get(key) else {
        return -2;
    };
    let json = value.get_deep_value().to_json();
    write_out(out, cap, json.as_bytes())
}

#[cfg(test)]
mod tests {
    use super::*;

    const CONTAINER: *const c_char = b"docs\0".as_ptr().cast();

    fn set(handle: usize, key: &[u8], json: &[u8]) -> i32 {
        let mut key = key.to_vec();
        let mut json = json.to_vec();
        key.push(0);
        json.push(0);
        lob_map_set(handle, CONTAINER, key.as_ptr().cast(), json.as_ptr().cast())
    }

    fn read(handle: usize, key: &[u8]) -> String {
        let mut key = key.to_vec();
        key.push(0);
        let len = lob_read_entry(handle, CONTAINER, key.as_ptr().cast(), std::ptr::null_mut(), 0);
        let mut buffer = vec![0u8; len as usize];
        assert_eq!(
            lob_read_entry(handle, CONTAINER, key.as_ptr().cast(), buffer.as_mut_ptr(), buffer.len()),
            len
        );
        String::from_utf8(buffer).unwrap()
    }

    #[test]
    fn snapshot_round_trip() {
        let handle = lob_new();
        lob_subscribe_root(handle);
        for index in 0..100 {
            let key = format!("k{index}").into_bytes();
            let value = format!("{{\"n\":{index},\"pad\":\"xxxxxxxxxxxxxxxx\"}}").into_bytes();
            assert_eq!(set(handle, &key, &value), 0);
        }
        assert_eq!(lob_commit(handle), 0);
        assert!(lob_last_event_count(handle) >= 1);

        let len = lob_export_snapshot(handle, std::ptr::null_mut(), 0);
        assert!(len > 0, "snapshot export must report a size");
        let mut buffer = vec![0u8; len as usize];
        assert_eq!(lob_export_snapshot(handle, buffer.as_mut_ptr(), buffer.len()), len);

        let other = lob_new();
        assert_eq!(lob_import(other, buffer.as_ptr(), buffer.len()), 0);
        assert!(read(other, b"k42").contains("\"n\":42"), "imported snapshot must answer reads");
        assert!(lob_doc_size_ops(other) >= 100);
        lob_free(other);
        lob_free(handle);
    }

    #[test]
    fn updates_export_round_trip() {
        let handle = lob_new();
        assert_eq!(set(handle, b"a", b"1"), 0);
        assert_eq!(lob_commit(handle), 0);
        let len = lob_export_updates(handle, std::ptr::null_mut(), 0);
        assert!(len > 0);
        let mut buffer = vec![0u8; len as usize];
        assert_eq!(lob_export_updates(handle, buffer.as_mut_ptr(), buffer.len()), len);
        let other = lob_new();
        assert_eq!(lob_import(other, buffer.as_ptr(), buffer.len()), 0);
        assert_eq!(read(other, b"a"), "1");
        lob_free(other);
        lob_free(handle);
    }
}
