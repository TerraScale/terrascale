//! Smoke proof for the production auth chunk; all credentials are fixtures.
use super::*;
use bytes::Bytes;
use http_body_util::{BodyExt, Full};
use hyper::{service::service_fn, Request, Response};
use hyper_util::rt::TokioIo;
use std::sync::Mutex;

#[tokio::test]
async fn exchanges_project_key_refreshes_once_and_reuses_shared_cache() {
    const PROJECT: &str = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
    let url = format!("http://{}", listener.local_addr().unwrap());
    let server = tokio::spawn(async move {
        let seen = Arc::new(Mutex::new(Vec::new()));
        let grant = |token| {
            serde_json::json!({"access_token":token,"token_type":"Bearer","expires_in":900})
                .to_string()
        };
        for (status, body) in [
            (200, grant("old")),
            (401, "{}".into()),
            (200, grant("new")),
            (200, "{}".into()),
            (200, "{}".into()),
        ] {
            let (socket, _) = listener.accept().await.unwrap();
            let requests = seen.clone();
            let service = service_fn(move |req: Request<hyper::body::Incoming>| {
                let (requests, body) = (requests.clone(), body.clone());
                async move {
                    let (parts, bytes) = req.into_parts();
                    let bytes = bytes.collect().await.unwrap().to_bytes();
                    requests
                        .lock()
                        .unwrap()
                        .push((parts.uri.to_string(), parts.headers, bytes));
                    Ok::<_, std::convert::Infallible>(
                        Response::builder()
                            .status(status)
                            .body(Full::new(Bytes::from(body)))
                            .unwrap(),
                    )
                }
            });
            hyper::server::conn::http1::Builder::new()
                .keep_alive(false)
                .serve_connection(TokioIo::new(socket), service)
                .await
                .unwrap();
        }
        seen
    });
    let key = format!("{PROJECT}_{}", "a".repeat(52));
    let mut client = Client::for_project_api_key(PROJECT, &key)
        .endpoint(&url)
        .build()
        .unwrap();
    let Auth::Key(auth) = &mut Arc::get_mut(&mut client.inner).unwrap().auth else {
        unreachable!()
    };
    Arc::get_mut(auth).unwrap().endpoint = format!("{url}/terrascale/oauth/token").parse().unwrap();
    let request = Req::new(Method::POST, vec!["v1", "databases"])
        .json(&serde_json::json!({"name":"orders"}))
        .unwrap();
    client.send(request).await.unwrap();
    client
        .clone()
        .send(Req::new(Method::GET, vec!["v1", "databases"]))
        .await
        .unwrap();
    let seen = tokio::time::timeout(Duration::from_secs(3), server)
        .await
        .unwrap()
        .unwrap();
    let requests = seen.lock().unwrap();
    assert_eq!(requests.len(), 5);
    for index in [0, 2] {
        assert_eq!(requests[index].0, "/terrascale/oauth/token");
        assert_eq!(
            requests[index].2,
            format!("grant_type=client_credentials&project={PROJECT}")
        );
        assert!(requests[index].1[AUTHORIZATION]
            .to_str()
            .unwrap()
            .starts_with("Basic "));
    }
    for (index, token) in [(1, "old"), (3, "new"), (4, "new")] {
        assert_eq!(
            requests[index].0,
            format!("/v1/projects/{PROJECT}/databases")
        );
        assert_eq!(requests[index].1[AUTHORIZATION], format!("Bearer {token}"));
    }
    assert_eq!(requests[1].2, requests[3].2);
    assert!(!format!("{client:?}").contains(&key));
}
