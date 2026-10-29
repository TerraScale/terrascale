use terrascale::{ErrorKind, ReqwestTransport};

#[test]
fn invalid_custom_ca_fails_before_transport() {
    for pem in [
        &b""[..],
        &b"not a PEM certificate"[..],
        &b"-----BEGIN CERTIFICATE-----\nAAAA\n-----END CERTIFICATE-----\n"[..],
    ] {
        let error = ReqwestTransport::with_root_certificates(pem).unwrap_err();
        assert_eq!(error.kind, ErrorKind::Validation);
        assert_eq!(error.code, "invalid_root_certificate");
    }
}

#[tokio::test]
async fn project_https_requires_the_configured_root() {
    use bytes::Bytes;
    use http_body_util::Full;
    use hyper::{service::service_fn, Request, Response};
    use hyper_util::rt::TokioIo;
    use std::sync::{
        atomic::{AtomicUsize, Ordering},
        Arc,
    };
    use terrascale::v1::{Client, Error, RetryPolicy};

    let mut params = rcgen::CertificateParams::new(Vec::<String>::new()).unwrap();
    params.is_ca = rcgen::IsCa::Ca(rcgen::BasicConstraints::Unconstrained);
    let ca =
        rcgen::CertifiedIssuer::self_signed(params, rcgen::KeyPair::generate().unwrap()).unwrap();
    let leaf_key = rcgen::KeyPair::generate().unwrap();
    let leaf = rcgen::CertificateParams::new(vec!["localhost".into(), "127.0.0.1".into()])
        .unwrap()
        .signed_by(&leaf_key, &ca)
        .unwrap();
    let config = rustls::ServerConfig::builder_with_provider(Arc::new(
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
    let acceptor = tokio_rustls::TlsAcceptor::from(Arc::new(config));
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let endpoint = format!("https://{}", listener.local_addr().unwrap());
    let hits = Arc::new(AtomicUsize::new(0));
    let seen = hits.clone();
    let task = tokio::spawn(async move {
        while let Ok((socket, _)) = listener.accept().await {
            let (acceptor, seen) = (acceptor.clone(), seen.clone());
            tokio::spawn(async move {
                let Ok(tls) = acceptor.accept(socket).await else {
                    return;
                };
                let service = service_fn(move |request: Request<hyper::body::Incoming>| {
                    seen.fetch_add(1, Ordering::SeqCst);
                    assert_eq!(request.uri(), "/v1/projects/01ARZ3NDEKTSV4RRFFQ69G5FAV/databases/main/collections/users/documents/by-key?id=00000000000000000000000000");
                    assert_eq!(
                        request.headers()["authorization"],
                        "Bearer fixture-access-token"
                    );
                    async {
                        Ok::<_, std::convert::Infallible>(Response::builder().status(404)
                        .header("content-type", "application/json")
                        .body(Full::new(Bytes::from_static(br#"{"error":{"code":"not_found","message":"Document not found"}}"#))).unwrap())
                    }
                });
                let _ = hyper::server::conn::http1::Builder::new()
                    .serve_connection(TokioIo::new(tls), service)
                    .await;
            });
        }
    });
    for trusted in [false, true] {
        let mut builder = Client::builder(&endpoint, "fixture-access-token")
            .project("01ARZ3NDEKTSV4RRFFQ69G5FAV")
            .retry_policy(RetryPolicy::none());
        if trusted {
            builder = builder.add_root_ca_pem(ca.pem());
        }
        let error = builder
            .build()
            .unwrap()
            .database("main")
            .collection("users")
            .get::<serde_json::Value>(terrascale::v1::DocumentId::nil())
            .await
            .unwrap_err();
        if trusted {
            assert!(error.is_not_found(), "{error:?}");
        } else {
            assert!(matches!(error, Error::Transport(_)), "{error:?}");
        }
    }
    assert_eq!(hits.load(Ordering::SeqCst), 1);
    task.abort();
}
