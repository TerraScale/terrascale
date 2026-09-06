use std::io::{Read, Write};
use std::net::TcpListener;
use terrascale::{HttpTransport, Method, Request, ReqwestTransport};

#[tokio::test]
async fn production_transport_returns_redirects_without_replaying_mutations() {
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let address = listener.local_addr().unwrap();
    let server = std::thread::spawn(move || {
        for status in [307, 308] {
            let (mut stream, _) = listener.accept().unwrap();
            let mut request = [0; 4096];
            assert!(stream.read(&mut request).unwrap() > 0);
            write!(stream, "HTTP/1.1 {status} Redirect\r\nLocation: http://127.0.0.1:9/mutation\r\nContent-Length: 0\r\nConnection: close\r\n\r\n").unwrap();
        }
    });
    for (transport, status) in [
        (ReqwestTransport::new(), 307),
        (ReqwestTransport::default(), 308),
    ] {
        let response = transport
            .send(Request {
                route_name: "documents.put".into(),
                method: Method::Put,
                url: format!("http://{address}/mutation"),
                headers: vec![("authorization".into(), "Bearer secret".into())],
                body: Some(b"payload".to_vec()),
            })
            .await
            .unwrap();
        assert_eq!(response.status, status);
    }
    server.join().unwrap();
}
