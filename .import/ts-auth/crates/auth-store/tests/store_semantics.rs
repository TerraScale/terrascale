use std::{
    collections::HashMap,
    io::{Read, Write},
    net::TcpListener,
    sync::{
        Arc, Mutex,
        atomic::{AtomicBool, Ordering},
        mpsc,
    },
    thread,
    time::Duration,
};

use auth_contracts::models::{
    AuditEvent, Instance, OrgMembership, Organization, OrganizationStatus, RefreshToken,
    ScimDirectory, ScimDirectoryStatus, Subscription, User, VerificationToken,
};
use auth_store::{AuthStore, QueryTransport, RefreshRotateOutcome, ScimActivationOutcome};
use serde_json::{Value, json};
use terrascale::{ClientConfig, TerraScaleClient};

#[derive(Clone)]
struct Document {
    value: Value,
    version: u64,
}

type PauseDelete = Arc<Mutex<Option<(mpsc::Sender<()>, mpsc::Receiver<()>)>>>;
type PausePut = Arc<Mutex<Option<(String, String, mpsc::Sender<()>, mpsc::Receiver<()>)>>>;
type PauseGet = PausePut;

struct PutFault {
    collection: String,
    id: String,
    commit: bool,
}

struct Fixture {
    origin: String,
    documents: Arc<Mutex<HashMap<(String, String), Document>>>,
    stop: Arc<AtomicBool>,
    pause_delete: PauseDelete,
    pause_put: PausePut,
    pause_get: PauseGet,
    put_fault: Arc<Mutex<Option<PutFault>>>,
    thread: Option<thread::JoinHandle<()>>,
}

impl Fixture {
    fn start() -> Self {
        let listener = TcpListener::bind("127.0.0.1:0").expect("test listener");
        listener
            .set_nonblocking(true)
            .expect("nonblocking listener");
        let origin = format!(
            "http://{}",
            listener.local_addr().expect("listener address")
        );
        let documents = Arc::new(Mutex::new(HashMap::new()));
        let stop = Arc::new(AtomicBool::new(false));
        let pause_delete: PauseDelete = Arc::new(Mutex::new(None));
        let pause_put: PausePut = Arc::new(Mutex::new(None));
        let pause_get: PauseGet = Arc::new(Mutex::new(None));
        let put_fault = Arc::new(Mutex::new(None::<PutFault>));
        let records = Arc::clone(&documents);
        let signal = Arc::clone(&stop);
        let pause = Arc::clone(&pause_delete);
        let put_pause = Arc::clone(&pause_put);
        let get_pause = Arc::clone(&pause_get);
        let fault = Arc::clone(&put_fault);
        let thread = thread::spawn(move || {
            while !signal.load(Ordering::SeqCst) {
                match listener.accept() {
                    Ok((mut stream, _)) => {
                        let records = Arc::clone(&records);
                        let pause = Arc::clone(&pause);
                        let put_pause = Arc::clone(&put_pause);
                        let get_pause = Arc::clone(&get_pause);
                        let fault = Arc::clone(&fault);
                        thread::spawn(move || {
                            stream
                                .set_read_timeout(Some(Duration::from_secs(5)))
                                .expect("read timeout");
                            let mut received = Vec::new();
                            let (header_end, content_len) = loop {
                                let mut chunk = [0_u8; 4096];
                                let count = stream.read(&mut chunk).expect("read request");
                                assert!(count > 0, "request closed early");
                                received.extend_from_slice(&chunk[..count]);
                                if let Some(end) =
                                    received.windows(4).position(|slice| slice == b"\r\n\r\n")
                                {
                                    let headers = String::from_utf8_lossy(&received[..end]);
                                    let len = headers
                                        .lines()
                                        .find_map(|line| {
                                            line.to_ascii_lowercase()
                                                .strip_prefix("content-length:")
                                                .and_then(|value| {
                                                    value.trim().parse::<usize>().ok()
                                                })
                                        })
                                        .unwrap_or(0);
                                    if received.len() >= end + 4 + len {
                                        break (end, len);
                                    }
                                }
                            };
                            let headers =
                                String::from_utf8_lossy(&received[..header_end]).to_string();
                            let body = &received[header_end + 4..header_end + 4 + content_len];
                            if headers.starts_with("DELETE ")
                                && headers.contains("/collections/uniques/")
                                && let Some((ready, resume)) =
                                    pause.lock().expect("pause lock").take()
                            {
                                ready.send(()).expect("signal paused delete");
                                resume
                                    .recv_timeout(Duration::from_secs(5))
                                    .expect("resume delete");
                            }
                            wait_for_target_put(&headers, &put_pause);
                            let (status, response, etag) =
                                respond_with_fault(&headers, body, &records, &fault);
                            wait_for_target_get_after_snapshot(&headers, &get_pause);
                            let phrase = if status >= 400 { "Error" } else { "OK" };
                            let response_text = response.to_string();
                            let etag_header = etag
                                .map_or(String::new(), |value| format!("ETag: \"{value}\"\r\n"));
                            let wire = format!(
                                "HTTP/1.1 {status} {phrase}\r\nContent-Type: application/json\r\nContent-Length: {}\r\n{etag_header}Connection: close\r\n\r\n{response_text}",
                                response_text.len()
                            );
                            stream.write_all(wire.as_bytes()).expect("write response");
                        });
                    }
                    Err(error) if error.kind() == std::io::ErrorKind::WouldBlock => {
                        thread::sleep(Duration::from_millis(2));
                    }
                    Err(error) => panic!("accept failed: {error}"),
                }
            }
        });
        Self {
            origin,
            documents,
            stop,
            pause_delete,
            pause_put,
            pause_get,
            put_fault,
            thread: Some(thread),
        }
    }

    fn store(&self) -> AuthStore {
        let client = TerraScaleClient::new(
            ClientConfig::builder()
                .api_base_url(&self.origin)
                .api_key("test-key")
                .build()
                .expect("SDK config"),
        );
        let query =
            QueryTransport::new(&self.origin, "Bearer test-key", None, None).expect("query config");
        AuthStore::new(client, query, "db_test".into(), Some(vec![7; 32]), vec![])
    }

    fn change(&self, collection: &str, id: &str, f: impl FnOnce(&mut Value)) {
        let mut docs = self.documents.lock().expect("documents lock");
        let doc = docs
            .get_mut(&(collection.into(), id.into()))
            .expect("document exists");
        f(&mut doc.value);
        doc.version += 1;
    }

    fn remove(&self, collection: &str, id: &str) {
        self.documents
            .lock()
            .expect("documents lock")
            .remove(&(collection.into(), id.into()));
    }

    fn count_documents(&self, collection: &str) -> usize {
        self.documents
            .lock()
            .expect("documents lock")
            .keys()
            .filter(|(name, _)| name == collection)
            .count()
    }

    fn pause_next_claim_delete(&self) -> (mpsc::Receiver<()>, mpsc::Sender<()>) {
        let (ready_tx, ready_rx) = mpsc::channel();
        let (resume_tx, resume_rx) = mpsc::channel();
        *self.pause_delete.lock().expect("pause lock") = Some((ready_tx, resume_rx));
        (ready_rx, resume_tx)
    }

    fn pause_next_put(&self, collection: &str, id: &str) -> (mpsc::Receiver<()>, mpsc::Sender<()>) {
        let (ready_tx, ready_rx) = mpsc::channel();
        let (resume_tx, resume_rx) = mpsc::channel();
        *self.pause_put.lock().expect("pause put lock") =
            Some((collection.into(), id.into(), ready_tx, resume_rx));
        (ready_rx, resume_tx)
    }

    fn pause_next_get_after_snapshot(
        &self,
        collection: &str,
        id: &str,
    ) -> (mpsc::Receiver<()>, mpsc::Sender<()>) {
        let (ready_tx, ready_rx) = mpsc::channel();
        let (resume_tx, resume_rx) = mpsc::channel();
        *self.pause_get.lock().expect("pause get lock") =
            Some((collection.into(), id.into(), ready_tx, resume_rx));
        (ready_rx, resume_tx)
    }

    fn fail_next_put(&self, collection: &str, id: &str, commit: bool) {
        *self.put_fault.lock().expect("put fault lock") = Some(PutFault {
            collection: collection.into(),
            id: id.into(),
            commit,
        });
    }
}

fn wait_for_target_put(headers: &str, target: &PausePut) {
    let blocked_write = {
        let mut pending = target.lock().expect("pause put lock");
        if pending.as_ref().is_some_and(|(collection, id, _, _)| {
            headers.starts_with("PUT ")
                && headers.contains(&format!("/collections/{collection}/documents/{id}"))
        }) {
            pending.take()
        } else {
            None
        }
    };
    if let Some((_, _, ready, resume)) = blocked_write {
        ready.send(()).expect("signal paused put");
        resume
            .recv_timeout(Duration::from_secs(5))
            .expect("resume put");
    }
}

fn wait_for_target_get_after_snapshot(headers: &str, target: &PauseGet) {
    let requested = headers
        .lines()
        .next()
        .and_then(|line| line.split_whitespace().nth(1))
        .and_then(|path| path.split("/collections/").nth(1))
        .and_then(|tail| tail.split_once("/documents/"))
        .and_then(|(collection, encoded_id)| {
            url::form_urlencoded::parse(format!("id={encoded_id}").as_bytes())
                .next()
                .map(|(_, id)| (collection.to_owned(), id.into_owned()))
        });
    let blocked_read = {
        let mut pending = target.lock().expect("pause get lock");
        if pending.as_ref().is_some_and(|(collection, id, _, _)| {
            headers.starts_with("GET ")
                && requested.as_ref() == Some(&(collection.clone(), id.clone()))
        }) {
            pending.take()
        } else {
            None
        }
    };
    if let Some((_, _, ready, resume)) = blocked_read {
        ready.send(()).expect("signal paused get");
        resume
            .recv_timeout(Duration::from_secs(5))
            .expect("resume get");
    }
}

impl Drop for Fixture {
    fn drop(&mut self) {
        self.stop.store(true, Ordering::SeqCst);
        if let Some(thread) = self.thread.take() {
            thread.join().expect("fixture thread");
        }
    }
}

fn respond_with_fault(
    headers: &str,
    body: &[u8],
    records: &Arc<Mutex<HashMap<(String, String), Document>>>,
    fault: &Arc<Mutex<Option<PutFault>>>,
) -> (u16, Value, Option<u64>) {
    let injected = {
        let mut pending = fault.lock().expect("put fault lock");
        if pending.as_ref().is_some_and(|fault| {
            headers.starts_with("PUT ")
                && headers.contains(&format!(
                    "/collections/{}/documents/{}",
                    fault.collection, fault.id
                ))
        }) {
            pending.take()
        } else {
            None
        }
    };
    match injected {
        Some(PutFault { commit: true, .. }) => {
            let _ = respond(headers, body, records);
            (503, json!({"error":{"code":"injected_after_commit"}}), None)
        }
        Some(PutFault { commit: false, .. }) => (
            503,
            json!({"error":{"code":"injected_before_commit"}}),
            None,
        ),
        None => respond(headers, body, records),
    }
}

fn respond(
    headers: &str,
    body: &[u8],
    documents: &Arc<Mutex<HashMap<(String, String), Document>>>,
) -> (u16, Value, Option<u64>) {
    assert!(
        headers
            .lines()
            .any(|line| line.eq_ignore_ascii_case("authorization: Bearer test-key")),
        "request must use normalized bearer key"
    );
    let first = headers.lines().next().expect("request line");
    let mut words = first.split_whitespace();
    let method = words.next().expect("method");
    let path = words.next().expect("path");
    let tail = path.split("/collections/").nth(1).expect("collection path");
    let (collection, id) = tail.split_once("/documents").expect("document path");
    let id = id.strip_prefix('/').unwrap_or("");
    let id = url::form_urlencoded::parse(format!("id={id}").as_bytes())
        .next()
        .expect("decoded id")
        .1
        .into_owned();
    let key = (collection.to_owned(), id.clone());
    let mut docs = documents.lock().expect("documents lock");
    if method == "POST" && id == "query" {
        let request: Value = serde_json::from_slice(body).expect("query JSON");
        let predicates = request["predicates"].as_array().expect("predicates");
        let mut rows: Vec<_> = docs
            .iter()
            .filter(|((name, id), doc)| {
                name == collection
                    && request["after"]
                        .as_str()
                        .is_none_or(|cursor| id.as_str() > cursor)
                    && predicates.iter().all(|predicate| {
                        let field = predicate["path"].as_str().expect("predicate path");
                        let expected = &predicate["value"];
                        match predicate["operator"].as_str().expect("predicate operator") {
                            "equal" => &doc.value[field] == expected,
                            "greaterThanOrEqual" => doc.value[field]
                                .as_i64()
                                .zip(expected.as_i64())
                                .is_some_and(|(actual, boundary)| actual >= boundary),
                            operator => panic!("unexpected operator: {operator}"),
                        }
                    })
            })
            .map(|((_, id), doc)| json!({"id":id,"version":doc.version,"value":doc.value}))
            .collect();
        rows.sort_by(|left, right| left["id"].as_str().cmp(&right["id"].as_str()));
        let limit = request["limit"].as_u64().unwrap_or(100) as usize;
        let has_more = rows.len() > limit;
        rows.truncate(limit);
        let next_after = has_more.then(|| rows.last().unwrap()["id"].clone());
        return (
            200,
            json!({"documents":rows,"hasMore":has_more,"nextAfter":next_after}),
            None,
        );
    }
    if method == "GET" && id.is_empty() {
        let mut rows: Vec<_> = docs
            .iter()
            .filter(|((name, _), _)| name == collection)
            .map(|((_, id), doc)| json!({"id":id,"version":doc.version,"value":doc.value}))
            .collect();
        rows.sort_by(|left, right| left["id"].as_str().cmp(&right["id"].as_str()));
        return (200, json!({"documents":rows}), None);
    }
    match method {
        "GET" => docs
            .get(&key)
            .map_or((404, json!({"error":{"code":"not_found"}}), None), |doc| {
                (200, doc.value.clone(), Some(doc.version))
            }),
        "PUT" => {
            let value: Value = serde_json::from_slice(body).expect("document JSON");
            let create_only = headers
                .lines()
                .any(|line| line.eq_ignore_ascii_case("if-none-match: *"));
            if create_only && docs.contains_key(&key) {
                return (409, json!({"error":{"code":"conflict"}}), None);
            }
            if let Some(expected) = match_header(headers)
                && docs.get(&key).is_none_or(|doc| doc.version != expected)
            {
                return (412, json!({"error":{"code":"precondition_failed"}}), None);
            }
            let version = docs.get(&key).map_or(1, |doc| doc.version + 1);
            docs.insert(key, Document { value, version });
            (200, json!({"revision":version.to_string()}), Some(version))
        }
        "DELETE" => {
            if let Some(expected) = match_header(headers)
                && docs.get(&key).is_none_or(|doc| doc.version != expected)
            {
                return (412, json!({"error":{"code":"precondition_failed"}}), None);
            }
            if docs.remove(&key).is_some() {
                (200, Value::Null, None)
            } else {
                (404, json!({"error":{"code":"not_found"}}), None)
            }
        }
        _ => panic!("unexpected request {method} {path}"),
    }
}

fn match_header(headers: &str) -> Option<u64> {
    headers.lines().find_map(|line| {
        line.to_ascii_lowercase()
            .strip_prefix("if-match:")
            .and_then(|value| value.trim().trim_matches('"').parse().ok())
    })
}

fn user() -> User {
    serde_json::from_value(json!({"id":"user-1","instanceId":"inst","email":"a@example.com","emailKey":"inst:a@example.com","quotaSeat":0})).expect("user model")
}

fn instance() -> Instance {
    serde_json::from_value(
        json!({"id":"inst-1","slug":"tenant-one","name":"Tenant One","createdAt":1}),
    )
    .expect("instance model")
}

fn subscription(plan: &str) -> Subscription {
    serde_json::from_value(json!({
        "id":"sub-1", "instanceId":"inst", "stripeCustomerId":"cus-1",
        "plan":plan, "status":"active"
    }))
    .expect("subscription model")
}

#[tokio::test]
async fn instance_create_retry_reuses_only_its_own_claim_after_failed_write() {
    let fixture = Fixture::start();
    let store = fixture.store();
    fixture.fail_next_put("instances", "inst-1", false);
    assert!(store.create_instance(&instance()).await.is_err());
    assert!(store.get_instance("inst-1").await.expect("read").is_none());

    let claim_id = auth_store::unique_claim_id("instance-slug", "tenant-one");
    let claim: Value = store
        .get("uniques", &claim_id)
        .await
        .expect("claim read")
        .expect("claim retained");
    assert_eq!(claim["ownerId"], "inst-1");
    let created = store
        .create_instance(&instance())
        .await
        .expect("retry with same ID");
    assert_eq!(created.id, "inst-1");
    assert_eq!(fixture.count_documents("instances"), 1);

    let foreign = Instance {
        id: "inst-2".into(),
        ..instance()
    };
    assert!(store.create_instance(&foreign).await.is_err());
    assert!(store.get_instance("inst-2").await.expect("read").is_none());
}

#[tokio::test]
async fn instance_create_readback_recovers_ambiguous_commit() {
    let fixture = Fixture::start();
    let store = fixture.store();
    fixture.fail_next_put("instances", "inst-1", true);
    let created = store
        .create_instance(&instance())
        .await
        .expect("committed write readback");
    assert_eq!(created.id, "inst-1");
    let repeated = store
        .create_instance(&instance())
        .await
        .expect("same ID idempotent retry");
    assert_eq!(repeated.slug, "tenant-one");
    assert_eq!(fixture.count_documents("instances"), 1);
}

#[tokio::test]
async fn customer_metadata_cannot_change_or_orphan_quota_seat() {
    let fixture = Fixture::start();
    let store = fixture.store();
    store
        .claim_quota_seat("inst", 1, "user-1")
        .await
        .expect("claim seat");
    store.create_user(&user()).await.expect("create user");

    assert!(
        store
            .try_update_user("user-1", "inst", |user| {
                user.private_metadata =
                    serde_json::from_value(json!({"quotaSeat":999,"customer":"value"})).unwrap();
                true
            })
            .await
            .expect("replace customer metadata")
    );
    let stored = store
        .get_user("user-1")
        .await
        .expect("read user")
        .expect("user exists");
    assert_eq!(stored.quota_seat, Some(0));
    assert_eq!(stored.private_metadata["quotaSeat"], json!(999));

    assert!(
        store
            .try_update_user("user-1", "inst", |user| {
                user.quota_seat = Some(999);
                true
            })
            .await
            .is_err()
    );
    assert!(
        store
            .rekey_user("user-1", "inst", stored.email_key.clone(), None, |user| {
                user.quota_seat = None;
            })
            .await
            .is_err()
    );
    let stored = store
        .get_user("user-1")
        .await
        .expect("read user")
        .expect("user exists");
    assert_eq!(stored.quota_seat, Some(0));

    assert!(store.release_quota_seat_for_user(&stored).await.is_err());
    assert!(
        store
            .rekey_user("user-1", "inst", None, None, |user| user.deleted = true)
            .await
            .expect("tombstone user")
    );
    let deleted: User = store.get("users", "user-1").await.unwrap().unwrap();
    store
        .release_quota_seat_for_user(&deleted)
        .await
        .expect("release deleted owner's typed seat");
    assert_eq!(
        store
            .claim_quota_seat("inst", 1, "user-2")
            .await
            .expect("seat was freed"),
        0
    );
}

#[tokio::test]
async fn generic_unique_helpers_cannot_bypass_quota_ledger() {
    let fixture = Fixture::start();
    let store = fixture.store();
    assert!(
        store
            .claim_unique("quota-seat", "inst:0", "user-1")
            .await
            .is_err()
    );
    assert_eq!(
        store.claim_quota_seat("inst", 1, "user-1").await.unwrap(),
        0
    );
    assert!(store.release_unique("quota-seat", "inst:0").await.is_err());
    assert!(
        store
            .release_unique_if_owned("quota-seat", "inst:0", "user-1")
            .await
            .is_err()
    );
    let ledger: Value = store
        .get("quota_admissions", "inst")
        .await
        .unwrap()
        .unwrap();
    assert_eq!(ledger["count"], 1);
    assert_eq!(fixture.count_documents("uniques"), 1);
}

#[tokio::test]
async fn user_auth_epoch_advances_only_from_current_epoch() {
    let fixture = Fixture::start();
    let store = fixture.store();
    store.claim_quota_seat("inst", 1, "user-1").await.unwrap();
    store.create_user(&user()).await.unwrap();
    assert_eq!(
        store
            .advance_user_auth_epoch_if_current("other", "user-1", 0)
            .await
            .unwrap(),
        None
    );
    let attempts = [store.clone(), store.clone()].map(|store| {
        tokio::spawn(async move {
            store
                .advance_user_auth_epoch_if_current("inst", "user-1", 0)
                .await
        })
    });
    let [first, second] = attempts;
    let first = first.await;
    let second = second.await;
    assert_eq!(
        usize::from(matches!(&first, Ok(Ok(Some(1)))))
            + usize::from(matches!(&second, Ok(Ok(Some(1))))),
        1
    );
    assert_eq!(
        usize::from(matches!(&first, Ok(Ok(None)))) + usize::from(matches!(&second, Ok(Ok(None)))),
        1
    );
    assert_eq!(
        store.get_user("user-1").await.unwrap().unwrap().auth_epoch,
        1
    );
}

#[tokio::test]
async fn password_rekey_can_advance_epoch_and_rejects_old_proof() {
    let fixture = Fixture::start();
    let store = fixture.store();
    store.claim_quota_seat("inst", 1, "user-1").await.unwrap();
    store.create_user(&user()).await.unwrap();
    for (expected, password_hash) in [(0, Some("set-hash")), (1, None)] {
        assert!(
            store
                .rekey_user(
                    "user-1",
                    "inst",
                    Some("inst:a@example.com".into()),
                    None,
                    |user| {
                        user.password_hash = password_hash.map(str::to_owned);
                        user.auth_epoch += 1;
                    },
                )
                .await
                .unwrap()
        );
        let current = store.get_user("user-1").await.unwrap().unwrap();
        assert_eq!(current.auth_epoch, expected + 1);
        assert_eq!(current.password_hash.as_deref(), password_hash);
        assert_eq!(
            store
                .advance_user_auth_epoch_if_current("inst", "user-1", expected)
                .await
                .unwrap(),
            None
        );
    }
}

#[tokio::test]
async fn downgrade_counts_grandfathered_high_seats_before_new_low_seat_admission() {
    let fixture = Fixture::start();
    let store = fixture.store();
    for index in 0..99 {
        let owner_id = format!("old-{index:03}");
        let seat = 100 + index;
        store
            .create(
                "users",
                &owner_id,
                &json!({
                    "id":owner_id, "instanceId":"inst", "quotaSeat":seat,
                    "deleted":false
                }),
            )
            .await
            .unwrap();
        store
            .create(
                "uniques",
                &auth_store::unique_claim_id("quota-seat", &format!("inst:{seat}")),
                &json!({
                    "ownerType":"quota-seat", "ownerId":owner_id,
                    "instanceId":"inst", "seat":seat, "state":"active"
                }),
            )
            .await
            .unwrap();
    }
    let generation = store
        .begin_quota_cap_transition(
            "inst",
            10_000,
            100,
            "downgrade-1",
            &subscription("free"),
            None,
        )
        .await
        .unwrap();
    store
        .complete_quota_cap_transition("inst", "downgrade-1", generation)
        .await
        .unwrap();
    assert_eq!(
        store.claim_quota_seat("inst", 100, "new-1").await.unwrap(),
        0
    );
    assert!(store.claim_quota_seat("inst", 100, "new-2").await.is_err());
    let ledger: Value = store
        .get("quota_admissions", "inst")
        .await
        .unwrap()
        .unwrap();
    assert_eq!(ledger["count"], 100);
}

#[tokio::test]
async fn replaced_upgrade_never_opens_gate_or_accepts_reused_transition_id() {
    let fixture = Fixture::start();
    let store = fixture.store();
    let old_generation = store
        .begin_quota_cap_transition("inst", 1, 100, "old-upgrade", &subscription("pro"), None)
        .await
        .unwrap();
    let new_generation = store
        .replace_quota_cap_transition(
            "inst",
            "old-upgrade",
            old_generation,
            1,
            "new-downgrade",
            &subscription("free"),
            None,
        )
        .await
        .unwrap();
    assert!(new_generation > old_generation);
    assert_eq!(
        store.quota_admission_status("inst", 1).await.unwrap(),
        (0, 1)
    );
    assert!(
        store
            .complete_quota_cap_transition("inst", "old-upgrade", old_generation)
            .await
            .is_err()
    );
    store
        .complete_quota_cap_transition("inst", "new-downgrade", new_generation)
        .await
        .unwrap();
    assert_eq!(
        store.quota_admission_status("inst", 1).await.unwrap(),
        (0, 1)
    );
    assert!(
        store
            .begin_quota_cap_transition("inst", 1, 2, "old-upgrade", &subscription("pro"), None)
            .await
            .is_err()
    );
    assert_eq!(
        store.quota_admission_status("inst", 1).await.unwrap(),
        (0, 1)
    );
}

#[tokio::test]
async fn simultaneous_admissions_share_one_low_cap_gate() {
    let fixture = Fixture::start();
    let store = fixture.store();
    let barrier = Arc::new(tokio::sync::Barrier::new(3));
    let mut tasks = Vec::new();
    for owner in ["user-a", "user-b"] {
        let store = store.clone();
        let barrier = Arc::clone(&barrier);
        tasks.push(tokio::spawn(async move {
            barrier.wait().await;
            store.claim_quota_seat("inst", 1, owner).await
        }));
    }
    barrier.wait().await;
    let first = tasks.remove(0).await.unwrap();
    let second = tasks.remove(0).await.unwrap();
    assert_eq!(usize::from(first.is_ok()) + usize::from(second.is_ok()), 1);
    assert_eq!(fixture.count_documents("uniques"), 1);
    let ledger: Value = store
        .get("quota_admissions", "inst")
        .await
        .unwrap()
        .unwrap();
    assert_eq!(ledger["count"], 1);
}

#[tokio::test]
async fn ambiguous_admission_cas_and_release_retry_count_once() {
    let fixture = Fixture::start();
    let store = fixture.store();
    assert_eq!(
        store.claim_quota_seat("inst", 2, "user-1").await.unwrap(),
        0
    );
    store.create_user(&user()).await.unwrap();
    fixture.fail_next_put("quota_admissions", "inst", true);
    assert_eq!(
        store.claim_quota_seat("inst", 2, "user-2").await.unwrap(),
        1
    );
    let second = User {
        id: "user-2".into(),
        email: Some("b@example.com".into()),
        email_key: Some("inst:b@example.com".into()),
        quota_seat: Some(1),
        ..user()
    };
    store.create_user(&second).await.unwrap();
    assert!(
        store
            .rekey_user("user-2", "inst", None, None, |user| user.deleted = true)
            .await
            .unwrap()
    );
    let deleted: User = store.get("users", "user-2").await.unwrap().unwrap();
    store.release_quota_seat_for_user(&deleted).await.unwrap();
    store.release_quota_seat_for_user(&deleted).await.unwrap();
    let ledger: Value = store
        .get("quota_admissions", "inst")
        .await
        .unwrap()
        .unwrap();
    assert_eq!(ledger["count"], 1);
    assert_eq!(
        store.claim_quota_seat("inst", 2, "user-3").await.unwrap(),
        1
    );
}

#[tokio::test]
async fn stale_parallel_release_cannot_decrement_quota_twice() {
    let fixture = Fixture::start();
    let store = fixture.store();
    for (seat, owner) in [(0, "user-1"), (1, "user-2"), (2, "user-3")] {
        assert_eq!(
            store.claim_quota_seat("inst", 3, owner).await.unwrap(),
            seat
        );
        let mut record = user();
        record.id = owner.into();
        record.email = Some(format!("{owner}@example.com"));
        record.email_key = Some(format!("inst:{owner}@example.com"));
        record.quota_seat = Some(seat);
        store.create_user(&record).await.unwrap();
    }
    assert!(
        store
            .rekey_user("user-1", "inst", None, None, |user| user.deleted = true)
            .await
            .unwrap()
    );
    let deleted = store.get_user("user-1").await.unwrap().unwrap();
    let generation = store
        .begin_quota_cap_transition(
            "inst",
            3,
            2,
            "downgrade-release",
            &subscription("free"),
            None,
        )
        .await
        .unwrap();
    store
        .complete_quota_cap_transition("inst", "downgrade-release", generation)
        .await
        .unwrap();

    let claim_id = auth_store::unique_claim_id("quota-seat", "inst:0");
    let (ready, resume) = fixture.pause_next_get_after_snapshot("uniques", &claim_id);
    let stale_release = {
        let store = store.clone();
        let deleted = deleted.clone();
        tokio::spawn(async move { store.release_quota_seat_for_user(&deleted).await })
    };
    tokio::task::spawn_blocking(move || ready.recv_timeout(Duration::from_secs(5)))
        .await
        .unwrap()
        .expect("stale release read seat claim");
    store.release_quota_seat_for_user(&deleted).await.unwrap();
    assert_eq!(
        store.quota_admission_status("inst", 2).await.unwrap(),
        (2, 2)
    );
    resume.send(()).unwrap();
    stale_release.await.unwrap().unwrap();
    store.release_quota_seat_for_user(&deleted).await.unwrap();
    assert_eq!(
        store.quota_admission_status("inst", 2).await.unwrap(),
        (2, 2)
    );
    assert!(store.claim_quota_seat("inst", 2, "user-4").await.is_err());
}

#[tokio::test]
async fn interrupted_release_finishes_after_freed_seat_changes_owner() {
    let fixture = Fixture::start();
    let store = fixture.store();
    assert_eq!(
        store.claim_quota_seat("inst", 2, "user-1").await.unwrap(),
        0
    );
    store.create_user(&user()).await.unwrap();
    assert!(
        store
            .rekey_user("user-1", "inst", None, None, |user| user.deleted = true)
            .await
            .unwrap()
    );
    let deleted = store.get_user("user-1").await.unwrap().unwrap();
    let (ready, resume) = fixture.pause_next_claim_delete();
    let release = {
        let store = store.clone();
        let deleted = deleted.clone();
        tokio::spawn(async move { store.release_quota_seat_for_user(&deleted).await })
    };
    tokio::task::spawn_blocking(move || ready.recv_timeout(Duration::from_secs(5)))
        .await
        .unwrap()
        .expect("release marker committed before seat deletion");
    fixture.fail_next_put("quota_admissions", "inst", false);
    resume.send(()).unwrap();
    assert!(release.await.unwrap().is_err());
    assert_eq!(
        store.quota_admission_status("inst", 2).await.unwrap(),
        (1, 2)
    );
    assert_eq!(
        store.claim_quota_seat("inst", 2, "user-2").await.unwrap(),
        0
    );
    let mut replacement = user();
    replacement.id = "user-2".into();
    replacement.email = Some("b@example.com".into());
    replacement.email_key = Some("inst:b@example.com".into());
    store.create_user(&replacement).await.unwrap();
    store.release_quota_seat_for_user(&deleted).await.unwrap();
    store.release_quota_seat_for_user(&deleted).await.unwrap();
    assert_eq!(
        store.quota_admission_status("inst", 2).await.unwrap(),
        (1, 2)
    );
    assert!(store.get_user("user-2").await.unwrap().is_some());
}

#[tokio::test]
async fn downgrade_wins_paused_new_admission_cas() {
    let fixture = Fixture::start();
    let store = fixture.store();
    assert_eq!(
        store.claim_quota_seat("inst", 2, "user-1").await.unwrap(),
        0
    );
    store.create_user(&user()).await.unwrap();
    let (ready, resume) = fixture.pause_next_put("quota_admissions", "inst");
    let pending = {
        let store = store.clone();
        tokio::spawn(async move { store.claim_quota_seat("inst", 2, "user-2").await })
    };
    tokio::task::spawn_blocking(move || ready.recv_timeout(Duration::from_secs(5)))
        .await
        .unwrap()
        .expect("admission CAS reached storage");
    let generation = store
        .begin_quota_cap_transition("inst", 2, 1, "downgrade-1", &subscription("free"), None)
        .await
        .unwrap();
    resume.send(()).unwrap();
    assert!(pending.await.unwrap().is_err());
    store
        .complete_quota_cap_transition("inst", "downgrade-1", generation)
        .await
        .unwrap();
    let ledger: Value = store
        .get("quota_admissions", "inst")
        .await
        .unwrap()
        .unwrap();
    assert_eq!(ledger["count"], 1);
    assert_eq!(ledger["cap"], 1);
    assert!(
        store
            .get::<Value>(
                "uniques",
                &auth_store::unique_claim_id("quota-seat", "inst:1")
            )
            .await
            .unwrap()
            .is_none()
    );
}

#[tokio::test]
async fn expired_pending_seat_cannot_be_used_after_reconciliation_and_active_seat_is_retained() {
    let fixture = Fixture::start();
    let store = fixture.store();
    let mut unseated = user();
    unseated.quota_seat = None;
    assert!(
        store
            .create_user(&unseated)
            .await
            .expect_err("seat required")
            .is_precondition()
    );
    assert_eq!(
        store
            .claim_quota_seat("inst", 1, "user-1")
            .await
            .expect("seat claim"),
        0
    );
    let id = auth_store::unique_claim_id("quota-seat", "inst:0");
    fixture.change("uniques", &id, |value| value["createdAt"] = json!(0));
    let result = store
        .reconcile_quota_seats("inst")
        .await
        .expect("reconcile");
    assert_eq!((result.claims_examined, result.claims_removed), (1, 1));
    assert!(
        store
            .create_user(&user())
            .await
            .expect_err("stale seat rejected")
            .is_precondition()
    );
    assert!(
        store
            .get_user("user-1")
            .await
            .expect("user lookup")
            .is_none()
    );

    assert_eq!(
        store
            .claim_quota_seat("inst", 1, "user-1")
            .await
            .expect("new seat claim"),
        0
    );
    store
        .create_user(&user())
        .await
        .expect("user create activates seat");
    fixture.remove("users", "user-1");
    let result = store
        .reconcile_quota_seats("inst")
        .await
        .expect("reconcile active claim");
    assert_eq!(
        (
            result.claims_examined,
            result.claims_removed,
            result.claims_retained
        ),
        (1, 0, 1)
    );
    assert!(store.claim_quota_seat("inst", 1, "user-2").await.is_err());
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn activation_wins_race_with_stale_pending_claim_reconciliation() {
    let fixture = Fixture::start();
    let store = fixture.store();
    store
        .claim_quota_seat("inst", 1, "user-1")
        .await
        .expect("seat claim");
    let id = auth_store::unique_claim_id("quota-seat", "inst:0");
    fixture.change("uniques", &id, |value| value["createdAt"] = json!(0));
    let (ready, resume) = fixture.pause_next_claim_delete();
    let reconciling = {
        let store = store.clone();
        tokio::spawn(async move { store.reconcile_quota_seats("inst").await })
    };
    ready
        .recv_timeout(Duration::from_secs(5))
        .expect("reconciler reached delete");
    store
        .create_user(&user())
        .await
        .expect("activation should win");
    resume.send(()).expect("release stale delete");
    let result = reconciling
        .await
        .expect("reconcile task")
        .expect("reconcile result");
    assert_eq!((result.claims_examined, result.claims_removed), (1, 0));
    assert!(
        store
            .get_user("user-1")
            .await
            .expect("user lookup")
            .is_some()
    );
    assert!(store.claim_quota_seat("inst", 1, "user-2").await.is_err());
}

#[tokio::test]
async fn family_marker_blocks_replacement_after_rotation_and_audit_seal_detects_tampering() {
    let fixture = Fixture::start();
    let store = fixture.store();
    let original: RefreshToken = serde_json::from_value(json!({"id":"refresh-1","instanceId":"inst","clientId":"client","userId":"user-1","tokenKey":"hash-1","status":"active","scopes":["openid"],"createdAt":10,"expiresAt":1000,"familyId":"family-1"})).expect("refresh model");
    store
        .create_refresh_token(&original)
        .await
        .expect("initial refresh");
    assert!(matches!(
        store
            .rotate_refresh_token("hash-1", "inst", "client", 20, "refresh-2")
            .await
            .expect("rotate"),
        RefreshRotateOutcome::Rotated(_)
    ));
    store
        .revoke_refresh_family("inst", "client", "family-1")
        .await
        .expect("revoke family");
    let child = RefreshToken {
        id: "refresh-2".into(),
        token_key: "hash-2".into(),
        ..original
    };
    assert!(
        store
            .create_refresh_token(&child)
            .await
            .expect_err("revoked family blocks child")
            .is_precondition()
    );
    assert!(
        store
            .get_refresh_token("refresh-2")
            .await
            .expect("child lookup")
            .is_none()
    );

    let event: AuditEvent = serde_json::from_value(json!({"id":"event-1","instanceId":"inst","type":"user.created","actorId":"user-1","data":{"z":2,"a":1},"createdAt":42})).expect("audit model");
    let sealed = store
        .create_audit_event(&event)
        .await
        .expect("sealed event");
    assert!(store.has_valid_audit_integrity(&sealed));
    assert!(store.has_authentic_audit_integrity(&sealed));
    let mut tampered = sealed;
    tampered.data.insert("a".into(), json!(3));
    assert!(!store.has_valid_audit_integrity(&tampered));
    assert!(!store.has_authentic_audit_integrity(&tampered));
}

#[tokio::test]
async fn unique_claim_release_requires_exact_owner() {
    let fixture = Fixture::start();
    let store = fixture.store();
    store
        .claim_unique("user-email", "inst:a@example.com", "user-1")
        .await
        .expect("claim");
    assert!(
        !store
            .release_unique_if_owned("user-email", "inst:a@example.com", "user-2")
            .await
            .expect("foreign release")
    );
    assert!(
        store
            .claim_unique("user-email", "inst:a@example.com", "user-2")
            .await
            .expect_err("claim remains held")
            .is_conflict()
    );
    assert!(
        store
            .release_unique_if_owned("user-email", "inst:a@example.com", "user-1")
            .await
            .expect("owner release")
    );
    store
        .claim_unique("user-email", "inst:a@example.com", "user-2")
        .await
        .expect("new owner claim");
}

#[tokio::test]
async fn organization_tombstone_rejects_child_committing_after_deletion_sweep() {
    let fixture = Fixture::start();
    let store = fixture.store();
    let org: Organization = serde_json::from_value(json!({
        "id":"org-1", "instanceId":"inst", "name":"Team", "slugKey":"inst:team"
    }))
    .expect("organization model");
    store.create_organization(&org).await.expect("create org");
    store
        .create("users", "user-1", &user())
        .await
        .expect("create member user");
    let member: OrgMembership = serde_json::from_value(json!({
        "id":"member-1", "instanceId":"inst", "orgId":"org-1", "userId":"user-1",
        "memberKey":"org-1:user-1", "role":"member"
    }))
    .expect("membership model");
    let (ready, resume) = fixture.pause_next_put("memberships", "member-1");
    let writer = {
        let store = store.clone();
        tokio::spawn(async move { store.create_membership(&member).await })
    };
    tokio::task::spawn_blocking(move || ready.recv_timeout(Duration::from_secs(5)))
        .await
        .expect("pause waiter")
        .expect("membership write reached storage");
    store
        .delete_organization("org-1")
        .await
        .expect("delete org");
    assert_eq!(
        store
            .get_organization_for_deletion("org-1")
            .await
            .unwrap()
            .unwrap()
            .status,
        OrganizationStatus::Deleted
    );
    resume.send(()).expect("resume child write");
    assert!(
        writer
            .await
            .expect("writer join")
            .expect_err("late child rejected")
            .is_precondition()
    );
    assert!(store.get_organization("org-1").await.unwrap().is_none());
    assert!(store.get_membership("member-1").await.unwrap().is_none());
    assert_eq!(fixture.count_documents("memberships"), 0);
    store
        .delete_organization("org-1")
        .await
        .expect("idempotent cleanup retry");
}

#[tokio::test]
async fn old_timestamp_audit_commit_remains_pending_until_fanout_is_persisted() {
    let fixture = Fixture::start();
    let store = fixture.store();
    let event: AuditEvent = serde_json::from_value(json!({
        "id":"late-audit", "instanceId":"inst", "type":"user.created",
        "data":{"user_id":"user-1"}, "createdAt":1
    }))
    .expect("audit model");
    let sealed = store
        .create_audit_event(&event)
        .await
        .expect("create signed event");
    let raw: Value = store
        .get("audit_events", "late-audit")
        .await
        .unwrap()
        .unwrap();
    assert_eq!(raw["dispatchPending"], true);
    assert!(store.has_authentic_audit_integrity(&sealed));
    let pending = store
        .list_pending_audit_events("inst")
        .await
        .expect("pending scan");
    assert_eq!(pending.len(), 1);
    assert_eq!(pending[0].id, "late-audit");
    assert_eq!(
        store
            .list_pending_audit_events_since("inst", 1)
            .await
            .expect("eligible late event")
            .len(),
        1
    );
    assert!(
        store
            .list_pending_audit_events_since("inst", 2)
            .await
            .expect("before endpoint creation")
            .is_empty()
    );
    assert!(
        store
            .complete_audit_event_dispatch("late-audit")
            .await
            .unwrap()
    );
    assert!(
        store
            .list_pending_audit_events("inst")
            .await
            .unwrap()
            .is_empty()
    );
    let stored = store.get_audit_event("late-audit").await.unwrap().unwrap();
    assert!(store.has_authentic_audit_integrity(&stored));
    assert!(
        store
            .complete_audit_event_dispatch("late-audit")
            .await
            .unwrap()
    );
}

#[tokio::test]
async fn organization_cleanup_tombstones_directory_and_removes_scim_dependents() {
    let fixture = Fixture::start();
    let store = fixture.store();
    let org: Organization = serde_json::from_value(json!({
        "id":"org-1", "instanceId":"inst", "name":"Team", "slugKey":"inst:team"
    }))
    .unwrap();
    store.create_organization(&org).await.unwrap();
    let directory: ScimDirectory = serde_json::from_value(json!({
        "id":"dir-1", "instanceId":"inst", "orgId":"org-1", "tokenKey":"hashed-token"
    }))
    .unwrap();
    store.create_scim_directory(&directory).await.unwrap();
    store
        .create(
            "scim_user_links",
            "link-1",
            &json!({
                "id":"link-1", "instanceId":"inst", "directoryId":"dir-1", "userId":"user-1",
                "profileUserName":"A@Example.com", "profileInitialized":true
            }),
        )
        .await
        .unwrap();
    store
        .claim_unique("scim-user-name", "dir-1:a@example.com", "link-1")
        .await
        .unwrap();
    store
        .create(
            "scim_groups",
            "group-1",
            &json!({
                "id":"group-1", "instanceId":"inst", "directoryId":"dir-1",
                "displayName":"Team", "createdAt":1, "updatedAt":1
            }),
        )
        .await
        .unwrap();
    store
        .create(
            "scim_membership_claims",
            "org-1:user-1",
            &json!({
                "id":"org-1:user-1", "instanceId":"inst", "orgId":"org-1",
                "userId":"user-1", "membershipId":"member-1"
            }),
        )
        .await
        .unwrap();
    store.delete_organization("org-1").await.unwrap();
    assert_eq!(
        store
            .get_scim_directory("dir-1")
            .await
            .unwrap()
            .unwrap()
            .status,
        ScimDirectoryStatus::Deleted
    );
    assert!(
        store
            .get::<Value>("scim_user_links", "link-1")
            .await
            .unwrap()
            .is_none()
    );
    let retained_claim: Value = store
        .get(
            "uniques",
            &auth_store::unique_claim_id("scim-user-name", "dir-1:a@example.com"),
        )
        .await
        .unwrap()
        .expect("deleted directory keeps its name claim as an ABA fence");
    assert_eq!(retained_claim["ownerId"], "link-1");
    assert!(
        store
            .get::<Value>("scim_groups", "group-1")
            .await
            .unwrap()
            .is_none()
    );
    assert!(
        store
            .get::<Value>("scim_membership_claims", "org-1:user-1")
            .await
            .unwrap()
            .is_none()
    );
    assert_eq!(fixture.count_documents("scim_directories"), 1);
}

#[tokio::test]
async fn user_mutations_cannot_reopen_deleted_identity() {
    let fixture = Fixture::start();
    let store = fixture.store();
    store.claim_quota_seat("inst", 1, "user-1").await.unwrap();
    store.create_user(&user()).await.unwrap();
    assert!(
        store
            .rekey_user("user-1", "inst", None, None, |record| {
                record.deleted = true;
            })
            .await
            .unwrap()
    );
    assert!(
        !store
            .try_update_user("user-1", "inst", |record| {
                record.deleted = false;
                true
            })
            .await
            .unwrap()
    );
    assert!(
        !store
            .rekey_user("user-1", "inst", None, None, |record| {
                record.deleted = false;
            })
            .await
            .unwrap()
    );
    assert!(store.get_user("user-1").await.unwrap().unwrap().deleted);
}

#[tokio::test]
async fn provisional_scim_user_is_hidden_and_only_its_directory_can_activate() {
    let fixture = Fixture::start();
    let store = fixture.store();
    let org: Organization = serde_json::from_value(json!({
        "id":"org-1", "instanceId":"inst", "name":"Team", "slugKey":"inst:team"
    }))
    .unwrap();
    store.create_organization(&org).await.unwrap();
    let directory: ScimDirectory = serde_json::from_value(json!({
        "id":"dir-1", "instanceId":"inst", "orgId":"org-1", "tokenKey":"token-1"
    }))
    .unwrap();
    store.create_scim_directory(&directory).await.unwrap();
    store.claim_quota_seat("inst", 1, "user-1").await.unwrap();
    let provisional: User = serde_json::from_value(json!({
        "id":"user-1", "instanceId":"inst", "email":"a@example.com",
        "emailKey":"inst:a@example.com", "quotaSeat":0, "deleted":true,
        "scimProvisioning":{"directoryId":"dir-1","phase":"pending"}
    }))
    .unwrap();
    store.create_user(&provisional).await.unwrap();
    assert!(store.get_user("user-1").await.unwrap().is_none());
    assert!(
        store
            .find_user_by_email("inst", "a@example.com")
            .await
            .unwrap()
            .is_none()
    );
    assert!(
        store
            .list_users("inst", 100, None)
            .await
            .unwrap()
            .0
            .is_empty()
    );

    store
        .create(
            "scim_user_links",
            "dir-1:user-1",
            &json!({
                "id":"dir-1:user-1", "instanceId":"inst", "directoryId":"dir-1",
                "userId":"user-1", "active":true
            }),
        )
        .await
        .unwrap();
    let member: OrgMembership = serde_json::from_value(json!({
        "id":"member-1", "instanceId":"inst", "orgId":"org-1", "userId":"user-1",
        "memberKey":"org-1:user-1", "role":"member"
    }))
    .unwrap();
    assert!(
        store
            .create_membership(&member)
            .await
            .unwrap_err()
            .is_precondition()
    );
    assert!(
        store
            .create_membership_for_scim_directory(&member, "dir-other")
            .await
            .unwrap_err()
            .is_precondition()
    );
    store
        .create_membership_for_scim_directory(&member, "dir-1")
        .await
        .unwrap();
    assert_eq!(
        store
            .activate_scim_user("user-1", "dir-other")
            .await
            .unwrap(),
        ScimActivationOutcome::NoLongerEligible
    );
    assert_eq!(
        store.activate_scim_user("user-1", "dir-1").await.unwrap(),
        ScimActivationOutcome::Activated
    );
    assert_eq!(
        store.activate_scim_user("user-1", "dir-1").await.unwrap(),
        ScimActivationOutcome::AlreadyActiveOwned
    );
    assert!(
        store
            .get_user("user-1")
            .await
            .unwrap()
            .unwrap()
            .identity_established
    );
    assert!(
        !store
            .compensate_provisional_scim_user("user-1", "dir-1")
            .await
            .unwrap()
    );
}

#[tokio::test]
async fn exact_owner_compensation_removes_only_unactivated_scim_user() {
    let fixture = Fixture::start();
    let store = fixture.store();
    store.claim_quota_seat("inst", 1, "user-1").await.unwrap();
    let provisional: User = serde_json::from_value(json!({
        "id":"user-1", "instanceId":"inst", "email":"a@example.com",
        "emailKey":"inst:a@example.com", "quotaSeat":0, "deleted":true,
        "scimProvisioning":{"directoryId":"dir-1","phase":"pending"}
    }))
    .unwrap();
    store.create_user(&provisional).await.unwrap();
    assert!(
        !store
            .compensate_provisional_scim_user("user-1", "dir-other")
            .await
            .unwrap()
    );
    assert!(
        store
            .compensate_provisional_scim_user("user-1", "dir-1")
            .await
            .unwrap()
    );
    assert!(
        store
            .get::<User>("users", "user-1")
            .await
            .unwrap()
            .is_none()
    );
    assert!(
        store
            .get::<Value>(
                "uniques",
                &auth_store::unique_claim_id("user-email", "inst:a@example.com")
            )
            .await
            .unwrap()
            .is_none()
    );
    assert_eq!(fixture.count_documents("uniques"), 0);
}

#[tokio::test]
async fn compensation_fences_activation_before_releasing_claims() {
    let fixture = Fixture::start();
    let store = fixture.store();
    store.claim_quota_seat("inst", 1, "user-1").await.unwrap();
    let mut provisional = user();
    provisional.deleted = true;
    provisional.scim_provisioning = Some(auth_contracts::models::ScimProvisioning {
        directory_id: "dir-1".into(),
        phase: auth_contracts::models::ScimProvisioningPhase::Pending,
    });
    store.create_user(&provisional).await.unwrap();
    let (ready, resume) = fixture.pause_next_claim_delete();
    let cleanup = {
        let store = store.clone();
        tokio::spawn(async move {
            store
                .compensate_provisional_scim_user("user-1", "dir-1")
                .await
        })
    };
    tokio::task::spawn_blocking(move || ready.recv_timeout(Duration::from_secs(5)))
        .await
        .unwrap()
        .expect("claim release reached storage");
    let hidden: User = store.get("users", "user-1").await.unwrap().unwrap();
    assert_eq!(
        hidden.scim_provisioning.unwrap().phase,
        auth_contracts::models::ScimProvisioningPhase::Cleaning
    );
    assert!(store.get_user("user-1").await.unwrap().is_none());
    resume.send(()).unwrap();
    assert!(cleanup.await.unwrap().unwrap());
    assert!(
        store
            .get::<User>("users", "user-1")
            .await
            .unwrap()
            .is_none()
    );
}

#[tokio::test]
async fn inactive_instance_wide_link_can_activate_hidden_user_but_cannot_authenticate() {
    let fixture = Fixture::start();
    let store = fixture.store();
    let directory: ScimDirectory = serde_json::from_value(json!({
        "id":"dir-1", "instanceId":"inst", "instanceWide":true,
        "tokenKey":"token-1"
    }))
    .unwrap();
    store.create_scim_directory(&directory).await.unwrap();
    store.claim_quota_seat("inst", 1, "user-1").await.unwrap();
    let mut provisional = user();
    provisional.deleted = true;
    provisional.scim_provisioning = Some(auth_contracts::models::ScimProvisioning {
        directory_id: "dir-1".into(),
        phase: auth_contracts::models::ScimProvisioningPhase::Pending,
    });
    store.create_user(&provisional).await.unwrap();
    store
        .create(
            "scim_user_links",
            "dir-1:user-1",
            &json!({
                "id":"dir-1:user-1", "instanceId":"inst", "directoryId":"dir-1",
                "userId":"user-1", "active":false
            }),
        )
        .await
        .unwrap();
    assert_eq!(
        store.activate_scim_user("user-1", "dir-1").await.unwrap(),
        ScimActivationOutcome::Activated
    );
    let active = store.get_user("user-1").await.unwrap().unwrap();
    assert_eq!(
        active.scim_instance_wide_directory_ids,
        vec!["dir-1".to_owned()]
    );
    assert!(!store.scim_auth_eligible(&active).await.unwrap());
    assert_eq!(
        store.activate_scim_user("user-1", "dir-1").await.unwrap(),
        ScimActivationOutcome::AlreadyActiveOwned
    );
    fixture.change("scim_user_links", "dir-1:user-1", |link| {
        link["active"] = json!(true);
    });
    assert!(store.scim_auth_eligible(&active).await.unwrap());
}

#[tokio::test]
async fn deleting_scim_link_cannot_authorize_its_owned_membership() {
    let fixture = Fixture::start();
    let store = fixture.store();
    let org: Organization = serde_json::from_value(json!({
        "id":"org-1", "instanceId":"inst", "name":"Team", "slugKey":"inst:team"
    }))
    .unwrap();
    store.create_organization(&org).await.unwrap();
    let directory: ScimDirectory = serde_json::from_value(json!({
        "id":"dir-1", "instanceId":"inst", "orgId":"org-1", "tokenKey":"token-1"
    }))
    .unwrap();
    store.create_scim_directory(&directory).await.unwrap();
    let member: OrgMembership = serde_json::from_value(json!({
        "id":"member-1", "instanceId":"inst", "orgId":"org-1", "userId":"user-1",
        "memberKey":"org-1:user-1", "role":"member"
    }))
    .unwrap();
    store
        .create("memberships", &member.id, &member)
        .await
        .unwrap();
    store
        .create(
            "scim_membership_claims",
            "org-1:user-1",
            &json!({
                "id":"org-1:user-1", "instanceId":"inst", "orgId":"org-1",
                "userId":"user-1", "membershipId":"member-1"
            }),
        )
        .await
        .unwrap();
    store
        .create(
            "scim_user_links",
            "dir-1:user-1",
            &json!({
                "id":"dir-1:user-1", "instanceId":"inst", "directoryId":"dir-1",
                "userId":"user-1", "active":true
            }),
        )
        .await
        .unwrap();
    assert!(store.org_membership_auth_eligible(&member).await.unwrap());
    fixture.change("scim_user_links", "dir-1:user-1", |link| {
        link["deleting"] = json!(true);
        link["active"] = json!(false);
    });
    assert!(!store.org_membership_auth_eligible(&member).await.unwrap());
    fixture.change("scim_user_links", "dir-1:user-1", |link| {
        link["deleting"] = json!(false);
        link["cleanupComplete"] = json!(true);
        link["active"] = json!(true);
    });
    assert!(!store.org_membership_auth_eligible(&member).await.unwrap());
    let manual = OrgMembership {
        id: "manual-member".into(),
        ..member.clone()
    };
    assert!(store.org_membership_auth_eligible(&manual).await.unwrap());
    fixture.change("organizations", "org-1", |org| {
        org["status"] = json!("deleting");
    });
    assert!(!store.org_membership_auth_eligible(&manual).await.unwrap());
}

#[tokio::test]
async fn instance_wide_scim_authority_denies_until_link_is_active_and_releases_on_deletion() {
    let fixture = Fixture::start();
    let store = fixture.store();
    store.create("users", "user-1", &user()).await.unwrap();
    let stale = store.get_user("user-1").await.unwrap().unwrap();
    let directory: ScimDirectory = serde_json::from_value(json!({
        "id":"dir-1", "instanceId":"inst", "instanceWide":true,
        "tokenKey":"token-1"
    }))
    .unwrap();
    store.create_scim_directory(&directory).await.unwrap();

    assert!(
        store
            .attach_instance_wide_scim_directory("user-1", "dir-1")
            .await
            .unwrap()
    );
    assert!(!store.scim_auth_eligible(&stale).await.unwrap());
    assert!(store.upsert_user(&stale).await.is_err());
    store
        .create(
            "scim_user_links",
            "dir-1:user-1",
            &json!({
                "id":"dir-1:user-1", "instanceId":"inst", "directoryId":"dir-1",
                "userId":"user-1", "active":false
            }),
        )
        .await
        .unwrap();
    assert!(!store.scim_auth_eligible(&stale).await.unwrap());
    fixture.change("scim_user_links", "dir-1:user-1", |link| {
        link["active"] = json!(true);
    });
    assert!(store.scim_auth_eligible(&stale).await.unwrap());
    fixture.change("users", "user-1", |user| user["banned"] = json!(true));
    assert!(!store.scim_auth_eligible(&stale).await.unwrap());
    fixture.change("users", "user-1", |user| user["banned"] = json!(false));
    let (mut deleting, version) = store
        .get_versioned::<ScimDirectory>("scim_directories", "dir-1")
        .await
        .unwrap()
        .unwrap();
    deleting.status = ScimDirectoryStatus::Deleting;
    assert!(
        store
            .compare_and_put("scim_directories", "dir-1", &deleting, &version)
            .await
            .unwrap()
    );
    assert!(!store.scim_auth_eligible(&stale).await.unwrap());
    store.delete_scim_directory("dir-1").await.unwrap();
    assert!(store.scim_auth_eligible(&stale).await.unwrap());
}

#[tokio::test]
async fn cleaning_tombstone_does_not_mask_a_reclaimed_email() {
    let fixture = Fixture::start();
    let store = fixture.store();
    let mut tombstone = user();
    tombstone.deleted = true;
    tombstone.scim_provisioning = Some(auth_contracts::models::ScimProvisioning {
        directory_id: "dir-1".into(),
        phase: auth_contracts::models::ScimProvisioningPhase::Cleaning,
    });
    store.create("users", "user-1", &tombstone).await.unwrap();
    let mut successor = user();
    successor.id = "user-2".into();
    store.create("users", "user-2", &successor).await.unwrap();
    assert_eq!(
        store
            .find_user_by_email("inst", "a@example.com")
            .await
            .unwrap()
            .unwrap()
            .id,
        "user-2"
    );
}

#[tokio::test]
async fn deleted_user_does_not_mask_a_reclaimed_email() {
    let fixture = Fixture::start();
    let store = fixture.store();
    let mut tombstone = user();
    tombstone.deleted = true;
    store.create("users", "user-1", &tombstone).await.unwrap();
    let mut successor = user();
    successor.id = "user-2".into();
    store.create("users", "user-2", &successor).await.unwrap();
    assert_eq!(
        store
            .find_user_by_email("inst", "a@example.com")
            .await
            .unwrap()
            .unwrap()
            .id,
        "user-2"
    );
}

#[tokio::test]
async fn token_consumption_selects_eligible_user_across_collisions_and_history() {
    let fixture = Fixture::start();
    let store = fixture.store();
    for index in 0..101 {
        let token: VerificationToken = serde_json::from_value(json!({
            "id":format!("old-{index:03}"), "instanceId":"inst", "userId":"user-1",
            "purpose":"passcode", "tokenKey":"shared-code", "expiresAt":i64::MAX,
            "consumed":true
        }))
        .unwrap();
        store.create_verification_token(&token).await.unwrap();
    }
    for (id, user_id) in [("target-1", "user-1"), ("target-2", "user-2")] {
        let token: VerificationToken = serde_json::from_value(json!({
            "id":id, "instanceId":"inst", "userId":user_id,
            "purpose":"passcode", "tokenKey":"shared-code", "expiresAt":i64::MAX
        }))
        .unwrap();
        store.create_verification_token(&token).await.unwrap();
    }
    assert_eq!(
        store
            .find_verification_token_matching("shared-code", |token| {
                token.user_id.as_deref() == Some("user-2") && !token.consumed
            })
            .await
            .unwrap()
            .unwrap()
            .id,
        "target-2"
    );
    let consumed = store
        .consume_verification_token("shared-code", |token| {
            token.instance_id == "inst"
                && token.user_id.as_deref() == Some("user-2")
                && !token.consumed
        })
        .await
        .unwrap()
        .unwrap();
    assert_eq!(consumed.id, "target-2");
    assert!(
        store
            .get_verification_token("target-2")
            .await
            .unwrap()
            .unwrap()
            .consumed
    );
    assert!(
        !store
            .get_verification_token("target-1")
            .await
            .unwrap()
            .unwrap()
            .consumed
    );
    let reissued = store
        .consume_verification_token("shared-code", |token| {
            token.instance_id == "inst"
                && token.user_id.as_deref() == Some("user-1")
                && !token.consumed
        })
        .await
        .unwrap()
        .unwrap();
    assert_eq!(reissued.id, "target-1");
}

#[tokio::test]
async fn tombstone_claim_release_retries_after_active_keys_were_cleared() {
    let fixture = Fixture::start();
    let store = fixture.store();
    store.claim_quota_seat("inst", 2, "user-1").await.unwrap();
    store
        .claim_unique("user-email", "inst:a@example.com", "user-1")
        .await
        .unwrap();
    store
        .claim_unique("user-username", "inst:alice", "user-1")
        .await
        .unwrap();
    store
        .claim_unique("user-email", "inst:other@example.com", "user-2")
        .await
        .unwrap();
    let mut deleted = user();
    deleted.deleted = true;
    deleted.username = Some("Alice".into());
    deleted.email_key = None;
    deleted.username_key = None;
    store.create("users", "user-1", &deleted).await.unwrap();
    assert!(
        store
            .release_deleted_user_claims("inst", "user-1")
            .await
            .unwrap()
    );
    assert!(
        store
            .release_deleted_user_claims("inst", "user-1")
            .await
            .unwrap()
    );
    for (kind, key) in [
        ("user-email", "inst:a@example.com"),
        ("user-username", "inst:alice"),
        ("quota-seat", "inst:0"),
    ] {
        assert!(
            store
                .get::<Value>("uniques", &auth_store::unique_claim_id(kind, key))
                .await
                .unwrap()
                .is_none()
        );
    }
    assert!(
        store
            .get::<Value>(
                "uniques",
                &auth_store::unique_claim_id("user-email", "inst:other@example.com")
            )
            .await
            .unwrap()
            .is_some()
    );
}

#[tokio::test]
async fn verified_account_wins_race_with_stale_unverified_deprovision() {
    let fixture = Fixture::start();
    let store = fixture.store();
    store.create("users", "user-1", &user()).await.unwrap();
    let (ready, resume) = fixture.pause_next_put("users", "user-1");
    let cleanup = {
        let store = store.clone();
        tokio::spawn(async move {
            store
                .deprovision_unverified_user_for_verified_claim("inst", "user-1", "a@example.com")
                .await
        })
    };
    tokio::task::spawn_blocking(move || ready.recv_timeout(Duration::from_secs(5)))
        .await
        .unwrap()
        .expect("unverified tombstone reached CAS");
    fixture.change("users", "user-1", |record| {
        record["emailVerified"] = json!(true);
    });
    resume.send(()).unwrap();
    assert!(!cleanup.await.unwrap().unwrap());
    let current = store.get_user("user-1").await.unwrap().unwrap();
    assert!(current.email_verified);
    assert!(!current.deleted);
}

#[tokio::test]
async fn verified_legacy_user_keeps_identity_when_email_becomes_unverified() {
    let fixture = Fixture::start();
    let store = fixture.store();
    let mut existing = user();
    existing.email_verified = true;
    store.create("users", "user-1", &existing).await.unwrap();
    assert!(
        store
            .rekey_user(
                "user-1",
                "inst",
                Some("inst:new@example.com".into()),
                None,
                |user| {
                    user.email = Some("new@example.com".into());
                    user.email_verified = false;
                },
            )
            .await
            .unwrap()
    );
    let current = store.get_user("user-1").await.unwrap().unwrap();
    assert!(!current.email_verified);
    assert!(current.identity_established);
    assert!(
        !store
            .deprovision_unverified_user_for_verified_claim_if_epoch(
                "inst",
                "user-1",
                "new@example.com",
                Some(current.auth_epoch),
            )
            .await
            .unwrap()
    );
    assert!(store.get_user("user-1").await.unwrap().is_some());
}

#[tokio::test]
async fn newer_auth_epoch_blocks_stale_mailbox_proof_tombstone() {
    let fixture = Fixture::start();
    let store = fixture.store();
    store.create("users", "user-1", &user()).await.unwrap();
    let (ready, resume) = fixture.pause_next_put("users", "user-1");
    let stale_claim = {
        let store = store.clone();
        tokio::spawn(async move {
            store
                .deprovision_unverified_user_for_verified_claim_if_epoch(
                    "inst",
                    "user-1",
                    "a@example.com",
                    Some(0),
                )
                .await
        })
    };
    tokio::task::spawn_blocking(move || ready.recv_timeout(Duration::from_secs(5)))
        .await
        .unwrap()
        .expect("proof tombstone reached User CAS");
    fixture.change("users", "user-1", |record| record["authEpoch"] = json!(1));
    resume.send(()).unwrap();
    assert!(!stale_claim.await.unwrap().unwrap());
    let current = store.get_user("user-1").await.unwrap().unwrap();
    assert_eq!(current.auth_epoch, 1);
    assert!(!current.deleted);
}

#[tokio::test]
async fn foreign_user_claim_conflict_releases_reserved_quota_seat() {
    let fixture = Fixture::start();
    let store = fixture.store();
    store.claim_quota_seat("inst", 1, "user-1").await.unwrap();
    store
        .claim_unique("user-email", "inst:a@example.com", "other-user")
        .await
        .unwrap();
    assert!(store.create_user(&user()).await.unwrap_err().is_conflict());
    assert_eq!(
        store.claim_quota_seat("inst", 1, "user-2").await.unwrap(),
        0
    );
}
