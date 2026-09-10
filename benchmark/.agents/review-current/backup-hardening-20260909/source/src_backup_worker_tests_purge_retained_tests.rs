use super::*;

#[tokio::test]
async fn purge_retained_requires_exact_historical_destination_coverage_before_mutation() {
    let (_, engine) = open_engine("purge-retained-destination-coverage");
    let engine = Arc::new(engine);
    let database = engine.database_id().to_string();
    let staging = location("purge-retained-destination-coverage-staging");
    let current = Arc::new(ObjectDestination::for_test(
        Provider::Oci,
        Arc::new(object_store::memory::InMemory::new()),
        4 * 1024 * 1024,
        8 * 1024 * 1024,
    ));
    let historical = Arc::new(ObjectDestination::for_test(
        Provider::Tigris,
        Arc::new(object_store::memory::InMemory::new()),
        4 * 1024 * 1024,
        8 * 1024 * 1024,
    ));
    let mut fleet = NativeBackupFleet::new(FleetLimits {
        streams: 1,
        staging_bytes_per_stream: 16 * 1024 * 1024,
        inventory_bytes_per_stream: 16 * 1024 * 1024,
        points_per_stream: 4,
    })
    .unwrap();
    fleet
        .attach(StreamConfig {
            engine: Arc::clone(&engine) as Arc<dyn BackupEngineHandle>,
            keys: keys(&engine),
            destinations: vec![(Provider::Oci, Arc::clone(&current))],
            historical_destinations: vec![(Provider::Tigris, Arc::clone(&historical))],
            policy_revision: "purge-retained".into(),
            retention_days: 1,
            staging,
            placement: PlacementLock::br_only(),
        })
        .unwrap();

    let stream = fleet.streams.get_mut(&database).unwrap();
    stream.closing = false;
    stream.health.protected = true;

    assert_eq!(
        fleet.purge_retained(&database, &[current.clone()]).await,
        Err(FleetError::Configuration)
    );
    assert!(!fleet.streams.get(&database).unwrap().closing);
    assert!(fleet.streams.get(&database).unwrap().health.protected);

    assert_eq!(
        fleet
            .purge_retained(&database, &[current.clone(), current.clone(), historical.clone()])
            .await,
        Err(FleetError::Configuration)
    );
    assert_eq!(
        fleet.purge_retained(&database, &[historical]).await,
        Err(FleetError::Configuration)
    );
    assert!(!fleet.streams.get(&database).unwrap().closing);
    assert!(fleet.streams.get(&database).unwrap().health.protected);
}
