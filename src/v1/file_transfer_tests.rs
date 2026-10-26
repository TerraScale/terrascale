use super::*;
use http_body_util::{BodyExt, Full};
use hyper::{service::service_fn, Request, Response};
use hyper_util::rt::TokioIo;
use std::sync::{Arc, Mutex};

struct Seen {
    method: Method,
    uri: String,
    headers: HeaderMap,
    body: Bytes,
}
struct Fixture {
    cap: FileSignedUrl,
    http: reqwest::Client,
    seen: Arc<Mutex<Vec<Seen>>>,
    task: tokio::task::JoinHandle<()>,
}
impl Drop for Fixture {
    fn drop(&mut self) {
        self.task.abort();
    }
}
impl Fixture {
    async fn new(status: u16, body: Bytes) -> Self {
        let mut params = rcgen::CertificateParams::new(Vec::<String>::new()).unwrap();
        params.is_ca = rcgen::IsCa::Ca(rcgen::BasicConstraints::Unconstrained);
        let ca = rcgen::CertifiedIssuer::self_signed(params, rcgen::KeyPair::generate().unwrap())
            .unwrap();
        let key = rcgen::KeyPair::generate().unwrap();
        let leaf = rcgen::CertificateParams::new(vec!["127.0.0.1".into()])
            .unwrap()
            .signed_by(&key, &ca)
            .unwrap();
        let config = rustls::ServerConfig::builder_with_provider(Arc::new(
            rustls::crypto::ring::default_provider(),
        ))
        .with_protocol_versions(&[&rustls::version::TLS13])
        .unwrap()
        .with_no_client_auth()
        .with_single_cert(
            vec![leaf.der().clone(), ca.der().clone()],
            rustls::pki_types::PrivateKeyDer::Pkcs8(key.serialize_der().into()),
        )
        .unwrap();
        let acceptor = tokio_rustls::TlsAcceptor::from(Arc::new(config));
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let url = format!(
            "https://{}/object?sig=fixture%2Fprivate%2Btoken",
            listener.local_addr().unwrap()
        );
        let http = storage_client()
            .add_root_certificate(reqwest::Certificate::from_pem(ca.pem().as_bytes()).unwrap())
            .build()
            .unwrap();
        let seen = Arc::new(Mutex::new(Vec::new()));
        let requests = seen.clone();
        let task = tokio::spawn(async move {
            let mut tasks = tokio::task::JoinSet::new();
            loop {
                tokio::select! {
                    accepted = listener.accept() => {
                        let (socket, _) = accepted.unwrap();
                        let (acceptor, seen, body) = (acceptor.clone(), requests.clone(), body.clone());
                        tasks.spawn(async move {
                            let Ok(tls) = acceptor.accept(socket).await else { return; };
                            let service = service_fn(move |req: Request<hyper::body::Incoming>| {
                                let (seen, body) = (seen.clone(), body.clone());
                                async move {
                                    let (parts, bytes) = req.into_parts();
                                    let body_in = bytes.collect().await.unwrap().to_bytes();
                                    seen.lock().unwrap().push(Seen { method:parts.method, uri:parts.uri.to_string(), headers:parts.headers, body:body_in });
                                    Ok::<_, std::convert::Infallible>(Response::builder().status(status)
                                        .header("location", "/must-not-follow?sig=fixture-private")
                                        .header("content-encoding", "gzip")
                                        .body(Full::new(body)).unwrap())
                                }
                            });
                            let _ = hyper::server::conn::http1::Builder::new().serve_connection(TokioIo::new(tls),service).await;
                        });
                    }
                    _ = tasks.join_next(), if !tasks.is_empty() => {}
                }
            }
        });
        let cap = FileSignedUrl {
            id: "01ARZ3NDEKTSV4RRFFQ69G5FAD".into(),
            url,
            expires_at: "2026-10-26T12:05:00Z".into(),
            headers: Default::default(),
        };
        Self {
            cap,
            http,
            seen,
            task,
        }
    }
}

#[tokio::test]
async fn transfers_exact_bytes_and_headers_without_project_auth_or_url_changes() {
    let mut fixture = Fixture::new(200, Bytes::from_static(b"hello")).await;
    fixture
        .cap
        .headers
        .insert("Content-Type".into(), "text/plain".into());
    fixture
        .cap
        .headers
        .insert("Content-Length".into(), "5".into());
    fixture.cap.headers.insert(
        "x-amz-checksum-sha256".into(),
        STANDARD.encode(Sha256::digest(b"hello")),
    );
    fixture
        .cap
        .transfer(&fixture.http, Some(Bytes::from_static(b"hello")))
        .await
        .unwrap();
    fixture.cap.headers.clear();
    fixture
        .cap
        .headers
        .insert("x-storage-token".into(), "fixture-private".into());
    assert_eq!(
        fixture.cap.transfer(&fixture.http, None).await.unwrap(),
        b"hello".as_slice()
    );
    let seen = fixture.seen.lock().unwrap();
    assert_eq!(seen.len(), 2);
    assert_eq!(seen[0].method, Method::PUT);
    assert_eq!(seen[0].body, b"hello".as_slice());
    assert_eq!(seen[0].headers["content-length"], "5");
    assert_eq!(seen[0].headers["content-type"], "text/plain");
    assert_eq!(seen[1].method, Method::GET);
    assert_eq!(seen[1].headers["x-storage-token"], "fixture-private");
    for request in seen.iter() {
        assert_eq!(request.uri, "/object?sig=fixture%2Fprivate%2Btoken");
        assert!(!request.headers.contains_key("authorization"));
        assert!(!request.headers.contains_key("cookie"));
    }
}

#[tokio::test]
async fn redirects_and_failures_are_redacted_and_never_retried() {
    for status in [302, 403, 500] {
        let fixture = Fixture::new(status, Bytes::from_static(b"fixture-private")).await;
        for data in [None, Some(Bytes::from_static(b"hello"))] {
            let error = fixture.cap.transfer(&fixture.http, data).await.unwrap_err();
            assert_eq!(error.status(), Some(status));
            assert!(!format!("{error:?} {error}").contains("fixture"));
            assert!(error.raw_body().is_none());
        }
        assert_eq!(fixture.seen.lock().unwrap().len(), 2);
    }
}

#[tokio::test]
async fn unsafe_urls_headers_and_mismatched_bytes_fail_before_network() {
    let fixture = Fixture::new(200, Bytes::new()).await;
    for url in [
        "http://127.0.0.1/object",
        "https://user:fixture-private@example.com/x",
        "https://example.com/x#secret",
    ] {
        let mut cap = fixture.cap.clone();
        cap.url = url.into();
        assert!(cap.transfer(&fixture.http, None).await.is_err());
    }
    for (name, value) in [
        ("Authorization", "Bearer fixture-private"),
        ("Host", "other.example"),
        ("x-storage", "a\r\nx-secret: injected"),
        ("Content-Length", "4"),
        ("x-amz-checksum-sha256", "wrong"),
    ] {
        let mut cap = fixture.cap.clone();
        cap.headers.insert(name.into(), value.into());
        let error = cap
            .transfer(&fixture.http, Some(Bytes::from_static(b"hello")))
            .await
            .unwrap_err();
        assert!(!format!("{error:?} {error}").contains("fixture-private"));
    }
    let mut cap = fixture.cap.clone();
    cap.headers
        .insert("Content-Type".into(), "text/plain".into());
    cap.headers
        .insert("content-type".into(), "application/json".into());
    assert!(cap.transfer(&fixture.http, None).await.is_err());
    assert!(fixture
        .cap
        .transfer(&fixture.http, Some(Bytes::new()))
        .await
        .is_err());
    assert!(fixture.seen.lock().unwrap().is_empty());
}

#[tokio::test]
async fn oversized_downloads_and_untrusted_certificates_fail_without_secret_diagnostics() {
    let fixture = Fixture::new(200, vec![0; MAX_BYTES + 1].into()).await;
    let error = fixture.cap.transfer(&fixture.http, None).await.unwrap_err();
    assert!(matches!(
        error,
        Error::ResponseTooLarge {
            limit: MAX_BYTES,
            may_have_applied: false,
            ..
        }
    ));
    assert!(!error.is_uncertain());
    let fixture = Fixture::new(200, Bytes::new()).await;
    let error = fixture.cap.get_bytes().await.unwrap_err();
    assert!(matches!(error, Error::Transport(_)));
    assert!(!format!("{error:?} {error}").contains("fixture"));
    assert!(fixture.seen.lock().unwrap().is_empty());
}
