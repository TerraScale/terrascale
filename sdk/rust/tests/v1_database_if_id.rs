//! Identity-conditioned branch and delete wire parity (HTTP fixture).

#[path = "support/project_http.rs"]
mod http;
use http::{reply, Fixture, PROJECT};
use terrascale::v1::Client;

const ID: &str = "01ARZ3NDEKTSV4RRFFQ69G5FAW";
const BRANCHED: &str = r#"{"id":"01ARZ3NDEKTSV4RRFFQ69G5FAX","projectId":"01ARZ3NDEKTSV4RRFFQ69G5FAV","name":"copy","branchedFrom":"01ARZ3NDEKTSV4RRFFQ69G5FAW","createdAt":"2026-10-22T12:00:00Z","region":"gru","homeRegion":"gru"}"#;

#[tokio::test]
async fn branch_and_delete_send_the_expected_id_segment() {
    let fixture = Fixture::new(vec![reply(201, BRANCHED), reply(204, "")]).await;
    let client: Client = fixture.client();
    let database = client.database("orders");
    let branched = database.branch_database_if_id("copy", ID).await.unwrap();
    database.delete_database_if_id(ID).await.unwrap();
    let seen = fixture.requests.lock().unwrap();
    assert_eq!(seen.len(), 2);
    assert_eq!(seen[0].method, "POST");
    assert_eq!(
        seen[0].uri,
        format!("/v1/projects/{PROJECT}/databases/orders/branches/if-id/{ID}")
    );
    assert_eq!(seen[0].body, br#"{"name":"copy"}"#.to_vec());
    assert_eq!(seen[1].method, "DELETE");
    assert_eq!(
        seen[1].uri,
        format!("/v1/projects/{PROJECT}/databases/orders/if-id/{ID}")
    );
    assert_eq!(branched.branched_from.as_deref(), Some(ID));
}
