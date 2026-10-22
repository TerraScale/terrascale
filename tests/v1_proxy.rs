//! A plain-HTTP loopback base URL must never be sent through an environment
//! proxy: that would carry the bearer key in plaintext off the host. This is
//! its own test binary because it sets process-wide proxy variables.

use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::Arc;

use serde_json::value::RawValue;
use terrascale::v1::*;
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::TcpListener;

/// Accepts connections, counts them and answers every request with a v1
/// `404 not_found`.
async fn listener(hits: Arc<AtomicUsize>, seen_auth: Arc<AtomicUsize>) -> u16 {
    let l = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let port = l.local_addr().unwrap().port();
    tokio::spawn(async move {
        while let Ok((mut s, _)) = l.accept().await {
            hits.fetch_add(1, Ordering::SeqCst);
            let seen_auth = seen_auth.clone();
            tokio::spawn(async move {
                // Read through the end of the headers (bounded, with a timeout).
                let mut head = Vec::new();
                let mut buf = [0u8; 1024];
                let read = async {
                    while !head.windows(4).any(|w| w == b"\r\n\r\n") && head.len() < 64 * 1024 {
                        match s.read(&mut buf).await {
                            Ok(0) | Err(_) => break,
                            Ok(n) => head.extend_from_slice(&buf[..n]),
                        }
                    }
                };
                let _ = tokio::time::timeout(std::time::Duration::from_secs(5), read).await;
                if String::from_utf8_lossy(&head)
                    .to_ascii_lowercase()
                    .contains("authorization: bearer fixture-access-token")
                {
                    seen_auth.fetch_add(1, Ordering::SeqCst);
                }
                if head.starts_with(b"GET ") {
                    let request = String::from_utf8_lossy(&head);
                    assert!(request.starts_with("GET /v1/projects/01ARZ3NDEKTSV4RRFFQ69G5FAV/databases/main/collections/items/documents/by-key?id="));
                }
                let body = r#"{"error":{"code":"not_found"}}"#;
                let resp = format!(
                    "HTTP/1.1 404 Not Found\r\ncontent-type: application/json\r\ncontent-length: {}\r\nconnection: close\r\n\r\n{body}",
                    body.len()
                );
                let _ = s.write_all(resp.as_bytes()).await;
            });
        }
    });
    port
}

#[tokio::test]
async fn loopback_http_bypasses_environment_proxies() {
    let (origin_hits, origin_auth) = (Arc::<AtomicUsize>::default(), Arc::<AtomicUsize>::default());
    let (proxy_hits, proxy_auth) = (Arc::<AtomicUsize>::default(), Arc::<AtomicUsize>::default());
    let origin = listener(origin_hits, origin_auth.clone()).await;
    let proxy = listener(proxy_hits.clone(), proxy_auth.clone()).await;
    // A proxy configured as a remote host would be. 192.0.2.0/24 (TEST-NET-1)
    // is unroutable, so ALL_PROXY can only fail if it is used.
    for (k, v) in [
        ("HTTP_PROXY", format!("http://127.0.0.1:{proxy}")),
        ("http_proxy", format!("http://127.0.0.1:{proxy}")),
        ("ALL_PROXY", "http://192.0.2.1:3128".to_owned()),
        ("all_proxy", "http://192.0.2.1:3128".to_owned()),
    ] {
        std::env::set_var(k, v);
    }
    std::env::remove_var("NO_PROXY");
    std::env::remove_var("no_proxy");

    let token = "fixture-access-token";
    for host in ["127.0.0.1", "localhost"] {
        let client = Client::builder(format!("http://{host}:{origin}"), token)
            .project("01ARZ3NDEKTSV4RRFFQ69G5FAV")
            .retry_policy(RetryPolicy::none())
            .timeout(Some(std::time::Duration::from_secs(5)))
            .build()
            .unwrap();
        let err = client
            .database("main")
            .collection("items")
            .get::<Box<RawValue>>(terrascale::v1::DocumentId::nil())
            .await
            .unwrap_err();
        assert!(err.is_not_found(), "{host}: {err:?}");
    }
    assert_eq!(
        origin_auth.load(Ordering::SeqCst),
        2,
        "both requests reached the origin directly"
    );
    assert_eq!(
        proxy_hits.load(Ordering::SeqCst),
        0,
        "the proxy saw no connection"
    );
    assert_eq!(proxy_auth.load(Ordering::SeqCst), 0);
}
