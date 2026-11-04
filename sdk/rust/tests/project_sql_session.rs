//! The SQL WebSocket session against a local socket that replays
//! `sdk/dotnet/contracts/terrabase/sql/websocket-session.json`.

use std::sync::{Arc, Mutex};

use futures_util::{SinkExt, StreamExt};
use serde_json::Value;
use terrascale::v1::{sql_error_codes, Client, Error, SqlStatement, SqlValue, SQL_SUBPROTOCOL};
use tokio::net::TcpListener;
use tokio_tungstenite::tungstenite::handshake::server::{ErrorResponse, Request, Response};
use tokio_tungstenite::tungstenite::http::{HeaderValue, StatusCode};
use tokio_tungstenite::tungstenite::Message;

const PROJECT: &str = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
const EXCHANGES: &str = include_str!(concat!(
    env!("CARGO_MANIFEST_DIR"),
    "/../dotnet/contracts/terrabase/sql/websocket-session.json"
));

#[derive(Default)]
struct Upgrade {
    path: String,
    authorization: Option<String>,
    protocol: Option<String>,
}

fn client(port: u16) -> Client {
    Client::builder(format!("http://127.0.0.1:{port}"), "session-token")
        .project(PROJECT)
        .build()
        .unwrap()
}

fn sql_error(error: Error) -> (Option<u16>, String, usize, Option<String>) {
    match error {
        Error::Sql { status, error } => (status, error.code, error.statement, error.sqlstate),
        other => panic!("expected a SQL error, got {other:?}"),
    }
}

#[tokio::test]
async fn session_replays_the_recorded_exchanges() {
    let exchanges: Value = serde_json::from_str(EXCHANGES).unwrap();
    let exchanges = exchanges["exchanges"].as_array().unwrap().clone();
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let port = listener.local_addr().unwrap().port();
    let upgrade = Arc::new(Mutex::new(Upgrade::default()));
    let seen = upgrade.clone();
    let server = tokio::spawn(async move {
        let (tcp, _) = listener.accept().await.unwrap();
        let callback = move |request: &Request, mut response: Response| {
            let header = |name: &str| {
                request
                    .headers()
                    .get(name)
                    .map(|value| value.to_str().unwrap().to_owned())
            };
            *seen.lock().unwrap() = Upgrade {
                path: request.uri().path().to_owned(),
                authorization: header("authorization"),
                protocol: header("sec-websocket-protocol"),
            };
            response.headers_mut().insert(
                "Sec-WebSocket-Protocol",
                HeaderValue::from_static(SQL_SUBPROTOCOL),
            );
            Ok::<_, ErrorResponse>(response)
        };
        let mut socket = tokio_tungstenite::accept_hdr_async(tcp, callback)
            .await
            .unwrap();
        for exchange in exchanges {
            let Some(Ok(Message::Text(text))) = socket.next().await else {
                panic!("expected a client frame for {}", exchange["client"]);
            };
            let mut frame: Value = serde_json::from_str(&text).unwrap();
            let id = frame.as_object_mut().unwrap().remove("id").unwrap();
            assert!(id.is_u64(), "{text}");
            assert_eq!(frame, exchange["client"]);
            let mut answer = exchange["server"].clone();
            answer["id"] = id;
            socket
                .send(Message::Text(answer.to_string().into()))
                .await
                .unwrap();
        }
        assert!(matches!(
            socket.next().await,
            Some(Ok(Message::Close(_))) | None
        ));
    });

    let session = client(port).database("orders").connect().await.unwrap();

    let rows = session
        .transaction(|s| async move {
            let inserted = s
                .execute("INSERT INTO ledger VALUES (?, ?)", &[2.into(), 25.into()])
                .await?;
            assert_eq!(
                (inserted.rows_affected, inserted.last_insert_rowid),
                (1, Some(2))
            );
            assert!(s.in_transaction());
            s.query("SELECT amount FROM ledger WHERE id = ?", &[2.into()])
                .await
        })
        .await
        .unwrap();
    assert_eq!(rows[0].get_i64("amount").unwrap(), 25);
    assert!(!session.in_transaction());

    session.begin().await.unwrap();
    let failed = session
        .execute("INSERT INTO ledger VALUES (1, 0)", &[])
        .await
        .unwrap_err();
    assert_eq!(
        sql_error(failed),
        (
            None,
            sql_error_codes::PRIMARY_KEY_VIOLATION.into(),
            0,
            Some("23505".into())
        )
    );
    assert!(session.in_transaction());
    let aborted = session
        .execute("SELECT count(*) FROM ledger", &[])
        .await
        .unwrap_err();
    assert_eq!(sql_error(aborted).1, sql_error_codes::TRANSACTION_ABORTED);
    let rolled_back = session.commit().await.unwrap_err();
    assert_eq!(
        sql_error(rolled_back),
        (
            None,
            sql_error_codes::TRANSACTION_ROLLED_BACK.into(),
            0,
            Some("40000".into())
        )
    );
    assert!(!session.in_transaction());

    session.begin().await.unwrap();
    let deleted = session.execute("DELETE FROM ledger", &[]).await.unwrap();
    assert_eq!(deleted.rows_affected, 2);
    session.rollback().await.unwrap();
    assert!(!session.in_transaction());

    let results = session
        .batch(vec![
            SqlStatement::new("SELECT ? AS data, ? AS name")
                .args([SqlValue::Blob(vec![1, 2, 3]), "Ada".into()]),
            "SELECT count(*) AS n FROM ledger".into(),
        ])
        .await
        .unwrap();
    let row = &results[0].records()[0];
    assert_eq!(row.get_bytes("data").unwrap(), [1, 2, 3]);
    assert_eq!(row.get_str("name").unwrap(), "Ada");
    assert_eq!(results[1].records()[0].get_i64("n").unwrap(), 2);

    session.close().await;
    assert!(matches!(
        session.execute("SELECT 1", &[]).await,
        Err(Error::InvalidInput(_))
    ));
    server.await.unwrap();

    let upgrade = upgrade.lock().unwrap();
    assert_eq!(
        upgrade.path,
        format!("/v1/projects/{PROJECT}/databases/orders/sql/ws")
    );
    assert_eq!(
        upgrade.authorization.as_deref(),
        Some("Bearer session-token")
    );
    assert_eq!(upgrade.protocol.as_deref(), Some(SQL_SUBPROTOCOL));
}

#[tokio::test]
async fn refused_upgrade_returns_the_http_auth_error() {
    let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
    let port = listener.local_addr().unwrap().port();
    let server = tokio::spawn(async move {
        let (tcp, _) = listener.accept().await.unwrap();
        let refuse = |_: &Request, _: Response| {
            let body = r#"{"error":{"code":"invalid_key","message":"the token is not valid"}}"#;
            let mut refusal = ErrorResponse::new(Some(body.to_owned()));
            *refusal.status_mut() = StatusCode::UNAUTHORIZED;
            refusal
                .headers_mut()
                .insert("Content-Type", HeaderValue::from_static("application/json"));
            refusal.headers_mut().insert(
                "Content-Length",
                HeaderValue::from_str(&body.len().to_string()).unwrap(),
            );
            Err(refusal)
        };
        assert!(tokio_tungstenite::accept_hdr_async(tcp, refuse)
            .await
            .is_err());
    });

    let error = client(port).database("orders").connect().await.unwrap_err();
    assert_eq!(error.status(), Some(401));
    assert_eq!(error.code(), Some("invalid_key"));
    assert!(matches!(error, Error::Response { status: 401, .. }));
    server.await.unwrap();
}
