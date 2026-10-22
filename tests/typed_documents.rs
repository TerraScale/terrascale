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
