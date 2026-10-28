use bytes::Bytes;
use futures_util::future::join_all;
use http_body_util::Full;
use hyper::{service::service_fn, Request, Response, Version};
use hyper_util::rt::{TokioExecutor, TokioIo};
use std::sync::{
    atomic::{AtomicUsize, Ordering},
    Arc,
};
use terrascale::{HttpTransport, Method, ReqwestTransport};

struct Server {
    endpoint: String,
    root: String,
    connections: Arc<AtomicUsize>,
    requests: Arc<AtomicUsize>,
    task: tokio::task::JoinHandle<()>,
}

impl Drop for Server {
    fn drop(&mut self) {
        self.task.abort();
    }
}

async fn server(version: Version) -> Server {
    let mut params = rcgen::CertificateParams::new(Vec::<String>::new()).unwrap();
    params.is_ca = rcgen::IsCa::Ca(rcgen::BasicConstraints::Unconstrained);
    let ca =
        rcgen::CertifiedIssuer::self_signed(params, rcgen::KeyPair::generate().unwrap()).unwrap();
    let leaf_key = rcgen::KeyPair::generate().unwrap();
    let leaf = rcgen::CertificateParams::new(vec!["127.0.0.1".into()])
        .unwrap()
        .signed_by(&leaf_key, &ca)
        .unwrap();
    let mut config = rustls::ServerConfig::builder_with_provider(Arc::new(
        rustls::crypto::ring::default_provider(),
    ))
    .with_protocol_versions(&[&rustls::version::TLS13])
    .unwrap()
    .with_no_client_auth()
    .with_single_cert(
        vec![leaf.der().clone(), ca.der().clone()],
        rustls::pki_types::PrivateKeyDer::Pkcs8(leaf_key.serialize_der().into()),
    )
    .unwrap();
    config.alpn_protocols = if version == Version::HTTP_2 {
        vec![b"h2".to_vec(), b"http/1.1".to_vec()]
    } else {
        vec![b"http/1.1".to_vec()]
    };
    let acceptor = tokio_rustls::TlsAcceptor::from(Arc::new(config));
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let endpoint = format!("https://{}", listener.local_addr().unwrap());
    let connections = Arc::new(AtomicUsize::new(0));
    let requests = Arc::new(AtomicUsize::new(0));
    let (accepted, seen) = (connections.clone(), requests.clone());
    let task = tokio::spawn(async move {
        while let Ok((socket, _)) = listener.accept().await {
            accepted.fetch_add(1, Ordering::SeqCst);
            let (acceptor, seen) = (acceptor.clone(), seen.clone());
            tokio::spawn(async move {
                let tls = acceptor.accept(socket).await.unwrap();
                let service = service_fn(move |request: Request<hyper::body::Incoming>| {
                    assert_eq!(request.version(), version);
                    assert_eq!(
                        request.headers()["authorization"],
                        "Bearer fixture-access-token"
                    );
                    seen.fetch_add(1, Ordering::SeqCst);
                    async {
                        Ok::<_, std::convert::Infallible>(Response::builder()
                            .status(404)
                            .header("content-type", "application/json")
                            .body(Full::new(Bytes::from_static(
                                br#"{"error":{"code":"not_found","message":"Document not found"}}"#,
                            )))
                            .unwrap())
                    }
                });
                if version == Version::HTTP_2 {
                    hyper::server::conn::http2::Builder::new(TokioExecutor::new())
                        .serve_connection(TokioIo::new(tls), service)
                        .await
                        .unwrap();
                } else {
                    hyper::server::conn::http1::Builder::new()
                        .serve_connection(TokioIo::new(tls), service)
                        .await
                        .unwrap();
                }
            });
        }
    });
    Server {
        endpoint,
        root: ca.pem(),
        connections,
        requests,
        task,
    }
}

async fn check_pool(version: Version) {
    let server = server(version).await;
    let transport = ReqwestTransport::with_root_certificates(server.root.as_bytes()).unwrap();
    let request = || terrascale::Request {
        route_name: "documents.get".into(),
        method: Method::Get,
        url: format!("{}/document", server.endpoint),
        headers: vec![("authorization".into(), "Bearer fixture-access-token".into())],
        body: None,
    };
    assert_eq!(transport.send(request()).await.unwrap().status, 404);
    if version == Version::HTTP_2 {
        for response in join_all((0..16).map(|_| transport.send(request()))).await {
            assert_eq!(response.unwrap().status, 404);
        }
    } else {
        for _ in 0..16 {
            assert_eq!(transport.send(request()).await.unwrap().status, 404);
        }
    }
    assert_eq!(server.connections.load(Ordering::SeqCst), 1);
    assert_eq!(server.requests.load(Ordering::SeqCst), 17);

    let client = terrascale::v1::Client::builder(&server.endpoint, "fixture-access-token")
        .project("01ARZ3NDEKTSV4RRFFQ69G5FAV")
        .retry_policy(terrascale::v1::RetryPolicy::none())
        .add_root_ca_pem(server.root.clone())
        .build()
        .unwrap();
    let read = || async {
        let error = client
            .database("main")
            .collection("users")
            .get::<serde_json::Value>(terrascale::v1::DocumentId::nil())
            .await
            .unwrap_err();
        assert!(error.is_not_found(), "{error:?}");
    };
    read().await;
    if version == Version::HTTP_2 {
        join_all((0..16).map(|_| read())).await;
    } else {
        for _ in 0..16 {
            read().await;
        }
    }
    assert_eq!(server.connections.load(Ordering::SeqCst), 2);
    assert_eq!(server.requests.load(Ordering::SeqCst), 34);
}

#[tokio::test]
async fn https_negotiates_http2_and_multiplexes_on_each_clients_connection() {
    tokio::time::timeout(
        std::time::Duration::from_secs(30),
        check_pool(Version::HTTP_2),
    )
    .await
    .unwrap();
}

#[tokio::test]
async fn https_falls_back_to_http1_and_reuses_each_clients_connection() {
    tokio::time::timeout(
        std::time::Duration::from_secs(30),
        check_pool(Version::HTTP_11),
    )
    .await
    .unwrap();
}
