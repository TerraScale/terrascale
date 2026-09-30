use serde_json::json;
use terrascale::*;

fn id<T: std::str::FromStr>(n: u8) -> T
where
    T::Err: std::fmt::Debug,
{
    format!("00000000-0000-0000-0000-{n:012x}").parse().unwrap()
}
fn scope() -> DatabaseScope {
    DatabaseScope::new(id(1), id(2), u64::MAX)
}
fn fields() -> Fields {
    Fields::new(vec![Field {
        id: 1,
        value: Value::I64(i64::MIN),
    }])
    .unwrap()
}
fn write() -> Write {
    Write {
        collection: id(3),
        document: id(4),
        schema_generation: DecimalU64(u64::MAX),
        schema_digest: Digest::from_bytes([1; 32]),
        observed: vec![],
        condition: Condition::Absent {},
        value: WriteValue::Live { fields: fields() },
    }
}
fn point() -> Point {
    let event = Event {
        database: id(2),
        shard: DecimalU64(u64::MAX),
        replica: id(5),
        incarnation: Incarnation::from_bytes([2; 16]),
        sequence: DecimalU64(1),
    };
    Point {
        version: ProtocolVersion,
        state: PointState::Value,
        context: vec![Observed {
            replica: event.replica,
            incarnation: event.incarnation,
            sequence: event.sequence,
        }],
        siblings: vec![Sibling {
            event,
            schema_generation: DecimalU64(1),
            schema_digest: Digest::from_bytes([1; 32]),
            value: SiblingValue::Live(fields()),
        }],
        state_version: Some(Digest::from_bytes([3; 32])),
    }
}

#[test]
fn canonical_values_preserve_integer_bytes_missing_and_null() {
    let values = Fields::new(vec![
        Field {
            id: 1,
            value: Value::Null,
        },
        Field {
            id: 2,
            value: Value::I64(i64::MIN),
        },
        Field {
            id: 3,
            value: Value::I64(i64::MAX),
        },
        Field {
            id: 4,
            value: Value::Bytes(vec![0, 128, 255]),
        },
        Field {
            id: 5,
            value: Value::Map(fields()),
        },
        Field {
            id: 6,
            value: Value::Array(vec![Value::Bool(true), Value::Utf8("é".into())]),
        },
    ])
    .unwrap();
    let encoded = serde_json::to_value(&values).unwrap();
    assert_eq!(encoded[1]["value"]["value"], i64::MIN.to_string());
    assert_eq!(encoded[3]["value"]["value"], "AID_");
    assert_eq!(serde_json::from_value::<Fields>(encoded).unwrap(), values);
    assert_eq!(values.get(1), Some(&Value::Null));
    assert_eq!(values.get(7), None);
    assert_eq!(
        serde_json::to_string(&DecimalU64(u64::MAX)).unwrap(),
        format!("\"{}\"", u64::MAX)
    );
}

#[test]
fn hostile_scalar_and_field_encodings_fail() {
    for input in [
        json!(7),
        json!("01"),
        json!("+1"),
        json!("18446744073709551616"),
    ] {
        assert!(serde_json::from_value::<DecimalU64>(input).is_err());
    }
    for input in [
        json!({"type":"null","value":null}),
        json!({"type":"i64","value":4}),
        json!({"type":"i64","value":"-0"}),
        json!({"type":"i64","value":"9223372036854775808"}),
        json!({"type":"bytes","value":"AA=="}),
        json!({"type":"bytes","value":"AB"}),
        json!({"type":"utf8","value":"ok","extra":1}),
    ] {
        assert!(
            serde_json::from_value::<Value>(input.clone()).is_err(),
            "accepted {input}"
        );
    }
    for ids in [vec![0], vec![2, 1], vec![1, 1]] {
        assert!(Fields::new(
            ids.into_iter()
                .map(|id| Field {
                    id,
                    value: Value::Null
                })
                .collect()
        )
        .is_err());
    }
    assert!("00000000-0000-0000-0000-00000000000A"
        .parse::<DocumentId>()
        .is_err());
    assert!("00000000000000000000000000000001"
        .parse::<DocumentId>()
        .is_err());
    assert!("AA".parse::<Hex<1>>().is_err());
}

#[test]
fn recursive_values_enforce_producer_bounds() {
    let mut value = Value::Null;
    for _ in 0..17 {
        value = Value::Array(vec![value]);
    }
    assert!(value.validate().is_err());
    assert!(Value::Array(vec![Value::Null; 1025]).validate().is_err());
    assert!(Value::Bytes(vec![0; 1024 * 1024 + 1]).validate().is_err());
}

#[test]
fn point_shapes_preserve_conflicts_and_require_nullable_token() {
    let mut point = point();
    point.validate_in(&scope()).unwrap();
    let mut encoded = serde_json::to_value(&point).unwrap();
    encoded.as_object_mut().unwrap().remove("state_version");
    assert!(serde_json::from_value::<Point>(encoded).is_err());
    point.state_version = None;
    assert!(point.validate_in(&scope()).is_err());
    let mut second = point.siblings[0].clone();
    second.event.replica = id(6);
    point.context.push(Observed {
        replica: second.event.replica,
        incarnation: second.event.incarnation,
        sequence: second.event.sequence,
    });
    point.siblings.push(second);
    point.state = PointState::Conflict;
    point.validate_in(&scope()).unwrap();
    assert_eq!(point.live_fields().unwrap_err().code, "document_conflict");
    let snapshot = SnapshotPage {
        version: ProtocolVersion,
        phase: SnapshotPhase::Snapshot,
        rows: vec![QueryRow {
            document: id(4),
            point: point.clone(),
        }],
        next_snapshot_cursor: None,
        poll_cursor: Some("opaque".into()),
    };
    let mut view = SubscriptionView::default();
    let snapshot_input = SnapshotRequest::new(
        Predicate::Equal {
            path: vec![1],
            value: Value::I64(i64::MIN),
        },
        128,
    )
    .unwrap();
    view.apply_snapshot(&scope(), id(3), &snapshot_input, &snapshot)
        .unwrap();
    assert_eq!(view.rows()[&id(4)].siblings.len(), 2);
    assert_eq!(
        view.rows()[&id(4)].live_fields().unwrap_err().code,
        "document_conflict"
    );
    let other_tenant = DatabaseScope::new(id(8), id(2), u64::MAX);
    assert!(view
        .apply_snapshot(&other_tenant, id(3), &snapshot_input, &snapshot)
        .is_err());
    let other = DatabaseScope::new(id(1), id(7), u64::MAX);
    assert!(point.validate_in(&other).is_err());
}

#[test]
fn predicates_distinguish_missing_null_and_scalar_comparisons() {
    assert!(Query::new(Predicate::IsMissing { path: vec![1] }, 128).is_ok());
    assert!(Query::new(Predicate::IsNull { path: vec![1] }, 1).is_ok());
    assert!(Query::new(
        Predicate::Equal {
            path: vec![1],
            value: Value::Null
        },
        1
    )
    .is_err());
    assert!(Query::new(
        Predicate::Equal {
            path: vec![0],
            value: Value::I64(1)
        },
        1
    )
    .is_err());
    assert!(Query::new(Predicate::IsNull { path: vec![1] }, 129).is_err());
}

#[test]
fn effect_digest_matches_independent_full_range_grammar_vector() {
    // Independent Python struct/u64LE/i64LE grammar check, not producer runtime proof.
    assert_eq!(
        canonical_effect_digest(&scope(), &Authority::Leaderless {}, &[write()])
            .unwrap()
            .to_string(),
        "80cc8d2dac2e807a4a841d15b100fe41b5d928be7dd0b4e8f980be1f4b75de96"
    );
    let first = PreparedTransaction::new(
        scope(),
        TransactionId::new(1000),
        Authority::Leaderless {},
        vec![write()],
    )
    .unwrap();
    let second = PreparedTransaction::new(
        scope(),
        TransactionId::new(2000),
        Authority::Leaderless {},
        vec![write()],
    )
    .unwrap();
    assert_eq!(first.effect_digest(), second.effect_digest());
    assert_ne!(first.body(), second.body());
    let mut changed = write();
    changed.condition = Condition::None {};
    assert_ne!(
        first.effect_digest(),
        canonical_effect_digest(&scope(), &Authority::Leaderless {}, &[changed]).unwrap()
    );
    assert!(PreparedTransaction::new(
        scope(),
        TransactionId::new(1),
        Authority::Leaderless {},
        vec![write(), write()]
    )
    .is_err());
    assert_eq!(
        PreparedTransaction::new(
            scope(),
            TransactionId::new(1),
            Authority::SingleWriter {
                generation: DecimalU64(1),
                writer: Writer {
                    replica: id(5),
                    incarnation: Incarnation::from_bytes([2; 16])
                }
            },
            vec![write()]
        )
        .unwrap_err()
        .code,
        "single_writer_not_admitted"
    );
}

#[test]
fn subscription_pages_publish_complete_groups_and_remove_from_view() {
    let mut view = SubscriptionView::default();
    let snapshot = SnapshotPage {
        version: ProtocolVersion,
        phase: SnapshotPhase::Snapshot,
        rows: vec![QueryRow {
            document: id(4),
            point: point(),
        }],
        next_snapshot_cursor: None,
        poll_cursor: Some("opaque".into()),
    };
    let snapshot_input = SnapshotRequest::new(
        Predicate::Equal {
            path: vec![1],
            value: Value::I64(i64::MIN),
        },
        128,
    )
    .unwrap();
    view.apply_snapshot(&scope(), id(3), &snapshot_input, &snapshot)
        .unwrap();
    let poll = PollPage {
        version: ProtocolVersion,
        phase: PollPhase::Poll,
        groups: vec![SubscriptionGroup {
            sequence: DecimalU64(8),
            effects: vec![
                SubscriptionEffect {
                    document: id(4),
                    point: None,
                },
                SubscriptionEffect {
                    document: id(7),
                    point: Some(point()),
                },
            ],
        }],
        poll_cursor: "next".into(),
    };
    let poll_input = PollRequest::new(snapshot_input.predicate.clone(), "opaque".into()).unwrap();
    view.apply_poll(&scope(), id(3), &poll_input, &poll)
        .unwrap();
    assert_eq!(view.rows().keys().copied().collect::<Vec<_>>(), vec![id(7)]);
    assert!(view
        .apply_poll(&scope(), id(3), &poll_input, &poll)
        .is_err());
    let mut invalid = poll.clone();
    invalid.groups[0].sequence = DecimalU64(9);
    invalid.groups[0].effects[1]
        .point
        .as_mut()
        .unwrap()
        .state_version = None;
    let next_input = PollRequest::new(snapshot_input.predicate, "next".into()).unwrap();
    assert!(view
        .apply_poll(&scope(), id(3), &next_input, &invalid)
        .is_err());
    assert_eq!(view.sequence(), Some(DecimalU64(8)));
    assert_eq!(view.rows().len(), 1);
    let mut invalid_snapshot = snapshot;
    invalid_snapshot.next_snapshot_cursor = Some("other".into());
    assert!(invalid_snapshot.validate_in(&scope(), 128).is_err());
}

#[test]
fn empty_projected_groups_advance_sequence_without_changing_rows() {
    let mut view = SubscriptionView::default();
    let predicate = Predicate::IsMissing { path: vec![1] };
    let snapshot_input = SnapshotRequest::new(predicate.clone(), 128).unwrap();
    let snapshot = SnapshotPage {
        version: ProtocolVersion,
        phase: SnapshotPhase::Snapshot,
        rows: vec![QueryRow {
            document: id(4),
            point: point(),
        }],
        next_snapshot_cursor: None,
        poll_cursor: Some("before".into()),
    };
    view.apply_snapshot(&scope(), id(3), &snapshot_input, &snapshot)
        .unwrap();
    let rows = view.rows().clone();
    let poll_input = PollRequest::new(predicate.clone(), "before".into()).unwrap();
    let poll = PollPage {
        version: ProtocolVersion,
        phase: PollPhase::Poll,
        groups: vec![SubscriptionGroup {
            sequence: DecimalU64(8),
            effects: vec![],
        }],
        poll_cursor: "after".into(),
    };
    poll.validate_in(&scope()).unwrap();
    view.apply_poll(&scope(), id(3), &poll_input, &poll)
        .unwrap();
    assert_eq!(view.rows(), &rows);
    assert_eq!(view.sequence(), Some(DecimalU64(8)));
    assert_eq!(view.poll_cursor(), Some("after"));
    let next_input = PollRequest::new(predicate, "after".into()).unwrap();
    assert!(view
        .apply_poll(&scope(), id(3), &next_input, &poll)
        .is_err());
    assert_eq!(view.rows(), &rows);
    assert_eq!(view.sequence(), Some(DecimalU64(8)));
    assert_eq!(view.poll_cursor(), Some("after"));
    let mut invalid = poll;
    invalid.groups[0].sequence = DecimalU64(0);
    assert!(invalid.validate_in(&scope()).is_err());
}

#[test]
fn shared_producer_golden_vectors_match_all_values_and_authority_hashes() {
    #[derive(serde::Deserialize)]
    #[serde(deny_unknown_fields)]
    struct Request {
        version: ProtocolVersion,
        transaction_id: TransactionId,
        authority: Authority,
        effect_digest: Digest,
        writes: Vec<Write>,
    }
    let fixture: serde_json::Value =
        serde_json::from_str(include_str!("../contracts/native/atomic_vectors.json")).unwrap();
    let vectors = fixture["vectors"].as_array().unwrap();
    assert_eq!(vectors.len(), 3);
    for vector in vectors {
        let scope = DatabaseScope {
            tenant: vector["scope"]["tenant"].as_str().unwrap().parse().unwrap(),
            database: vector["scope"]["database"]
                .as_str()
                .unwrap()
                .parse()
                .unwrap(),
            shard: vector["scope"]["shard"].as_str().unwrap().parse().unwrap(),
        };
        let request: Request = serde_json::from_value(vector["request"].clone()).unwrap();
        assert_eq!(request.version, ProtocolVersion);
        let expected: Digest = vector["effect_digest"].as_str().unwrap().parse().unwrap();
        assert_eq!(request.effect_digest, expected);
        assert_eq!(
            canonical_effect_digest(&scope, &request.authority, &request.writes).unwrap(),
            expected,
            "{}",
            vector["name"]
        );
        if matches!(request.authority, Authority::Leaderless {}) {
            let operation = PreparedTransaction::new(
                scope,
                request.transaction_id,
                request.authority,
                request.writes,
            )
            .unwrap();
            assert_eq!(
                serde_json::from_slice::<serde_json::Value>(operation.body()).unwrap(),
                vector["request"]
            );
        } else {
            assert_eq!(
                PreparedTransaction::new(
                    scope,
                    request.transaction_id,
                    request.authority,
                    request.writes
                )
                .unwrap_err()
                .code,
                "single_writer_not_admitted"
            );
        }
    }
}

#[test]
fn request_capacity_and_nullable_cursor_boundaries_are_strict() {
    let body = "x".repeat(4 * 1024 * 1024);
    assert_eq!(
        Request::json(
            "native.query",
            Method::Post,
            "https://gru.api.terrascale.tech".into(),
            &body
        )
        .unwrap_err()
        .code,
        "request_capacity"
    );
    assert!(
        serde_json::from_value::<QueryPage>(json!({"version":1,"rows":[],"next_cursor":null}))
            .is_err()
    );
    assert!(
        serde_json::from_value::<Authority>(json!({"mode":"leaderless","generation":"1"})).is_err()
    );
    assert!(serde_json::from_value::<Condition>(json!({"kind":"absent","version":"00"})).is_err());
    let mut sibling = serde_json::to_value(&point().siblings[0]).unwrap();
    sibling["kind"] = json!("tombstone");
    sibling["fields"] = serde_json::Value::Null;
    assert!(serde_json::from_value::<Sibling>(sibling).is_err());
}

#[test]
fn native_document_depth_array_and_exact_size_limits_match_producer() {
    fn maps(count: usize) -> Value {
        (0..count).fold(Value::Null, |value, _| {
            Value::Map(Fields::new(vec![Field { id: 1, value }]).unwrap())
        })
    }
    Fields::new(vec![Field {
        id: 1,
        value: maps(15),
    }])
    .unwrap()
    .validate()
    .unwrap();
    assert!(Fields::new(vec![Field {
        id: 1,
        value: maps(16)
    }])
    .is_err());
    assert!(Value::Array(vec![Value::Array(vec![])]).validate().is_err());
    let nested = Value::Array(vec![Value::Map(
        Fields::new(vec![Field {
            id: 1,
            value: Value::Array(vec![]),
        }])
        .unwrap(),
    )]);
    assert!(nested.validate().is_err());
    assert!(serde_json::from_value::<Value>(serde_json::to_value(nested).unwrap()).is_err());

    let scalar_max = 1024 * 1024 - 98 - 13;
    Fields::new(vec![Field {
        id: 1,
        value: Value::Bytes(vec![0; scalar_max]),
    }])
    .unwrap()
    .validate()
    .unwrap();
    assert!(Fields::new(vec![Field {
        id: 1,
        value: Value::Bytes(vec![0; scalar_max + 1])
    }])
    .unwrap()
    .validate()
    .is_err());
    assert!(Fields::new(vec![
        Field {
            id: 1,
            value: Value::Bytes(vec![0; 600_000])
        },
        Field {
            id: 2,
            value: Value::Bytes(vec![0; 600_000])
        }
    ])
    .unwrap()
    .validate()
    .is_err());

    // Root document111 + nested map2+9*1024. Do not charge root overhead
    // recursively: this valid case exceeds a fictitious inner document budget.
    let nested_max = 1024 * 1024 - 111 - 2 - 9 * 1024;
    let inner = Fields::new(
        (1..=1024)
            .map(|id| Field {
                id,
                value: if id == 1 {
                    Value::Bytes(vec![0; nested_max])
                } else {
                    Value::Null
                },
            })
            .collect(),
    )
    .unwrap();
    let outer = Fields::new(vec![Field {
        id: 1,
        value: Value::Map(inner),
    }])
    .unwrap();
    outer.validate().unwrap();
    serde_json::from_value::<Fields>(serde_json::to_value(outer).unwrap())
        .unwrap()
        .validate()
        .unwrap();
}

#[test]
fn subscription_view_rejects_unrelated_streams_and_invalid_phase_or_cursor() {
    let scope = scope();
    let predicate = Predicate::IsMissing { path: vec![1] };
    let first_input = SnapshotRequest::new(predicate.clone(), 1).unwrap();
    let first = SnapshotPage {
        version: ProtocolVersion,
        phase: SnapshotPhase::Snapshot,
        rows: vec![],
        next_snapshot_cursor: Some("snapshot2".into()),
        poll_cursor: None,
    };
    let mut view = SubscriptionView::default();
    view.apply_snapshot(&scope, id(3), &first_input, &first)
        .unwrap();
    let poll_input = PollRequest::new(predicate.clone(), "poll1".into()).unwrap();
    let poll = PollPage {
        version: ProtocolVersion,
        phase: PollPhase::Poll,
        groups: vec![],
        poll_cursor: "poll2".into(),
    };
    assert!(view.apply_poll(&scope, id(3), &poll_input, &poll).is_err());
    let mut next_input = first_input.clone();
    next_input.snapshot_cursor = Some("snapshot2".into());
    let complete = SnapshotPage {
        version: ProtocolVersion,
        phase: SnapshotPhase::Snapshot,
        rows: vec![],
        next_snapshot_cursor: None,
        poll_cursor: Some("poll1".into()),
    };
    assert!(view
        .apply_snapshot(&scope, id(9), &next_input, &complete)
        .is_err());
    let mut other_input = next_input.clone();
    other_input.predicate = Predicate::IsNull { path: vec![1] };
    assert!(view
        .apply_snapshot(&scope, id(3), &other_input, &complete)
        .is_err());
    assert!(view
        .apply_snapshot(&scope, id(3), &first_input, &complete)
        .is_err());
    view.apply_snapshot(&scope, id(3), &next_input, &complete)
        .unwrap();
    assert_eq!(view.poll_cursor(), Some("poll1"));
    let mut wrong_cursor = poll_input.clone();
    wrong_cursor.poll_cursor = "wrong".into();
    assert!(view
        .apply_poll(&scope, id(3), &wrong_cursor, &poll)
        .is_err());
    view.apply_poll(&scope, id(3), &poll_input, &poll).unwrap();
    assert_eq!(view.poll_cursor(), Some("poll2"));
    assert!(view
        .apply_snapshot(&scope, id(3), &first_input, &complete)
        .is_err());
}
