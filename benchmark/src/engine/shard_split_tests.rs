use super::shard_split::{RouteCatalog, RouteCatalogError, decode_catalog, encode_catalog};
use crate::shard_split::{RouteTable, ShardRoute};

#[test]
fn catalog_codec_round_trip_and_rejects_bad_inputs() {
    let table = RouteTable::new(vec![
        ShardRoute::new(1, None, Some(b"m".to_vec())).unwrap(),
        ShardRoute::new(2, Some(b"m".to_vec()), None).unwrap(),
    ])
    .unwrap();
    let catalog = RouteCatalog { table: table.with_generation(7), generation: 7 };
    let encoded = encode_catalog(&catalog).unwrap();
    assert_eq!(decode_catalog(&encoded).unwrap(), catalog);
    for bad in [vec![], vec![2], {
        let mut value = encoded.clone();
        value.push(0);
        value
    }] {
        assert_eq!(decode_catalog(&bad), Err(RouteCatalogError::Malformed));
    }
    let mut duplicate = encoded.clone();
    duplicate[30..38].copy_from_slice(&1_u64.to_be_bytes());
    assert_eq!(decode_catalog(&duplicate), Err(RouteCatalogError::Malformed));
}
