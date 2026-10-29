#![allow(dead_code)]

use bytes::Bytes;
use http_body_util::{BodyExt, Full};
use hyper::{service::service_fn, HeaderMap, Request, Response};
use hyper_util::rt::TokioIo;
use std::{
    collections::VecDeque,
    sync::{Arc, Mutex},
};
use terrascale::v1::{Client, RetryPolicy};

pub const PROJECT: &str = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
pub const DATABASE: &str = r#"{"id":"01ARZ3NDEKTSV4RRFFQ69G5FAW","projectId":"01ARZ3NDEKTSV4RRFFQ69G5FAV","name":"orders","branchedFrom":null,"createdAt":"2026-10-22T12:00:00Z","region":"gru","homeRegion":"gru","replicaRegions":["fra"]}"#;

pub struct Observed {
    pub method: String,
    pub uri: String,
    pub headers: HeaderMap,
    pub body: Vec<u8>,
}
pub struct Fixture {
    pub url: String,
    pub requests: Arc<Mutex<Vec<Observed>>>,
    task: tokio::task::JoinHandle<()>,
}
impl Drop for Fixture {
    fn drop(&mut self) {
        self.task.abort();
    }
}
impl Fixture {
    pub async fn new(replies: Vec<(u16, String)>) -> Self {
        Self::with_type(replies, "application/json").await
    }
    pub async fn with_type(replies: Vec<(u16, String)>, content_type: &'static str) -> Self {
        let listener = tokio::net::TcpListener::bind("127.0.0.1:0").await.unwrap();
        let url = format!("http://{}", listener.local_addr().unwrap());
        let requests = Arc::new(Mutex::new(Vec::new()));
        let (received, replies) = (
            requests.clone(),
            Arc::new(Mutex::new(VecDeque::from(replies))),
        );
        let task = tokio::spawn(async move {
            let mut connections = tokio::task::JoinSet::new();
            loop {
                tokio::select! {
                    accepted = listener.accept() => {
                        let (socket, _) = accepted.unwrap();
                        let (received, replies) = (received.clone(), replies.clone());
                        connections.spawn(async move {
                            let service = service_fn(move |request: Request<hyper::body::Incoming>| {
                                let (received, replies) = (received.clone(), replies.clone());
                                async move {
                                    let (parts, body) = request.into_parts();
                                    let body = body.collect().await.unwrap().to_bytes().to_vec();
                                    received.lock().unwrap().push(Observed { method: parts.method.to_string(), uri: parts.uri.to_string(), headers: parts.headers, body });
                                    let (status, body) = replies.lock().unwrap().pop_front().expect("unexpected request");
                                    Ok::<_, std::convert::Infallible>(Response::builder().status(status)
                                        .header("content-type", content_type).header("x-request-id", "parity-fixture")
                                        .body(Full::new(Bytes::from(body))).unwrap())
                                }
                            });
                            let _ = hyper::server::conn::http1::Builder::new().serve_connection(TokioIo::new(socket), service).await;
                        });
                    }
                    _ = connections.join_next(), if !connections.is_empty() => {}
                }
            }
        });
        Self {
            url,
            requests,
            task,
        }
    }
    pub fn client(&self) -> Client {
        Client::builder(&self.url, "fixture-token")
            .project(PROJECT)
            .retry_policy(RetryPolicy::none())
            .build()
            .unwrap()
    }
}
pub fn reply(status: u16, body: &str) -> (u16, String) {
    (status, body.into())
}
