use tb_core::{CollectionId, DatabaseId, DocumentId, ShardId, TenantId};
use tb_document::{
    DocumentCondition, DocumentScope, DocumentWrite, DocumentWriteValue, MutationAuthority,
    canonical_effect_digest,
};
use tb_format::{DocumentBinding, DocumentFieldInput, FieldType};
use tb_replication::CausalContext;
fn main() {
    let scope = DocumentScope {
        tenant: TenantId::parse_canonical("018f0000-0000-7000-8000-000000000001").unwrap(),
        database: DatabaseId::parse_canonical("018f0000-0000-7000-8000-000000000002").unwrap(),
        shard: ShardId::new(0),
    };
    let context = CausalContext::default();
    let fields = [DocumentFieldInput {
        field_id: 1,
        value_type: FieldType::Utf8,
        value: b"first note",
    }];
    let addresses = [
        (
            "018f0000-0000-7000-8000-000000000003",
            "018f0000-0000-7000-8000-000000000004",
        ),
        (
            "018f0000-0000-7000-8000-000000000003",
            "018f0000-0000-7000-8000-000000000006",
        ),
        (
            "018f0000-0000-7000-8000-000000000007",
            "018f0000-0000-7000-8000-000000000004",
        ),
    ];
    let writes: Vec<_> = addresses
        .iter()
        .map(|(collection, document)| DocumentWrite {
            binding: DocumentBinding {
                collection: CollectionId::parse_canonical(collection).unwrap(),
                document: DocumentId::parse_canonical(document).unwrap(),
                generation: 1,
                schema_digest: [0xaa; 32],
            },
            observed: &context,
            value: DocumentWriteValue::Live(&fields),
        })
        .collect();
    let digest = canonical_effect_digest(
        scope,
        MutationAuthority::Leaderless,
        &writes,
        &[DocumentCondition::Absent; 3],
    )
    .unwrap();
    println!(
        "{}",
        digest
            .iter()
            .map(|b| format!("{b:02x}"))
            .collect::<String>()
    );
    assert!(
        canonical_effect_digest(
            scope,
            MutationAuthority::Leaderless,
            &[writes[1], writes[0], writes[2]],
            &[DocumentCondition::Absent; 3]
        )
        .is_err()
    );
    assert!(
        canonical_effect_digest(
            scope,
            MutationAuthority::Leaderless,
            &[writes[2], writes[0], writes[1]],
            &[DocumentCondition::Absent; 3]
        )
        .is_err()
    );
}
