//! Streaming retention compared with complete sorting and exact accounting.

use super::*;
use crate::query::{
    OrderDirection, OrderingKey, QueryOrdering, QueryPaging, QueryPredicate, QueryProjection,
    QuerySource,
};
use crate::types::{Cancellation, CollectionName, Deadline};

fn query(
    direction: OrderDirection,
    cancellation: Option<Cancellation>,
    deadline: Option<Deadline>,
) -> NormalizedQuery {
    NormalizedQuery::from_sql(
        QuerySource::try_new(CollectionName::try_new("items").unwrap()).unwrap(),
        QueryPredicate::all(Vec::new()).unwrap(),
        QueryOrdering::try_new(vec![OrderingKey::try_new("value", direction).unwrap()]).unwrap(),
        QueryProjection::whole_document(),
        None,
        QueryPaging::try_new(3, Some(7)).unwrap(),
        deadline,
        cancellation,
    )
}

fn document(index: u32, payload: usize) -> Document {
    Document::new(
        DocumentId::try_new(&format!("doc-{index:04}")).unwrap(),
        CollectionName::try_new("items").unwrap(),
        DocumentValue::Object(
            [
                ("value".into(), DocumentValue::Integer(i64::from(index % 13))),
                ("payload".into(), DocumentValue::String("x".repeat(payload))),
            ]
            .into(),
        ),
    )
}

#[test]
fn broad_match_window_preserves_sort_offset_and_accounting() {
    let documents: Vec<_> = (0..512).rev().map(|index| document(index, 1024)).collect();
    for direction in [OrderDirection::Ascending, OrderDirection::Descending] {
        let query = query(direction, None, None);
        let capacity = retained_capacity(&query).unwrap();
        let (actual, matched, scanned, skipped) =
            collect_matches(&query, &documents, None, capacity).unwrap();
        let mut expected = documents.clone();
        expected.sort_by(|left, right| model::compare_documents(query.ordering(), left, right));
        assert_eq!(actual, expected[..capacity]);
        assert_eq!(actual[3..], expected[3..10]);
        assert_eq!((matched, scanned, skipped), (512, 512, 0));
    }
}

#[test]
fn scored_window_preserves_complete_sort_and_byte_accounting() {
    let documents: Vec<_> = (0..512).rev().map(|index| document(index, 1024)).collect();
    for direction in [OrderDirection::Ascending, OrderDirection::Descending] {
        let query = query(direction, None, None);
        let capacity = retained_capacity(&query).unwrap();
        let mut actual = Vec::new();
        let mut bytes = 0;
        let mut expected = Vec::new();
        for (index, document) in documents.iter().enumerate() {
            let score = f64::from(u32::try_from(index % 17).unwrap());
            retain_scored(&query, &mut actual, &mut bytes, document, score, direction, capacity)
                .unwrap();
            expected.push((score, document.clone()));
        }
        expected.sort_by(|(left_score, left), (right_score, right)| {
            compare_scores(
                direction,
                *left_score,
                left.id().as_str(),
                *right_score,
                right.id().as_str(),
            )
        });
        assert_eq!(actual, expected[..capacity]);
        assert_eq!(
            bytes,
            actual.iter().map(|(_, document)| document_bytes(document)).sum::<usize>()
        );
    }
}

#[test]
fn rejected_candidates_still_check_cancellation_and_deadline() {
    let cancellation = Cancellation::new();
    cancellation.cancel();
    let deadline = Deadline::after(std::time::Duration::from_nanos(1)).unwrap();
    while !deadline.is_expired() {
        std::hint::spin_loop();
    }
    for (signal, deadline, error) in [
        (Some(cancellation), None, QueryError::Cancelled),
        (None, Some(deadline), QueryError::Deadline),
    ] {
        let query = query(OrderDirection::Ascending, signal, deadline);
        let document = document(0, 0);
        assert_eq!(retain_candidate(&query, &mut Vec::new(), &mut 0, &document, 0), Err(error));
        assert_eq!(
            retain_scored(
                &query,
                &mut Vec::new(),
                &mut 0,
                &document,
                0.0,
                OrderDirection::Ascending,
                0
            ),
            Err(error)
        );
    }
}

#[test]
fn rejected_large_body_does_not_consume_retained_budget() {
    let query = query(OrderDirection::Ascending, None, None);
    let small = document(0, 0);
    let large = document(1, MAX_RETAINED_CANDIDATE_BYTES + 1);
    let mut retained = vec![small.clone()];
    let mut bytes = document_bytes(&small);
    retain_candidate(&query, &mut retained, &mut bytes, &large, 1).unwrap();
    assert_eq!(retained, [small.clone()]);
    assert_eq!(bytes, document_bytes(&small));
    let mut scored = vec![(0.0, small.clone())];
    retain_scored(&query, &mut scored, &mut bytes, &large, 1.0, OrderDirection::Ascending, 1)
        .unwrap();
    assert_eq!(scored, [(0.0, small)]);
    assert_eq!(
        retain_candidate(&query, &mut Vec::new(), &mut 0, &large, 1),
        Err(QueryError::CandidateBoundExceeded)
    );
    assert_eq!(
        retain_scored(&query, &mut Vec::new(), &mut 0, &large, 0.0, OrderDirection::Ascending, 1),
        Err(QueryError::CandidateBoundExceeded)
    );
}
