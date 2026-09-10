//! Focused construction tests for the production native backup owner.

#[cfg(test)]
mod tests {
    use crate::native_backup_runtime::{NativeBackupRuntime, NativeBackupRuntimeError};
    use terrabase::backup::worker::FleetLimits;

    #[test]
    fn rejects_invalid_fleet_limits_without_a_fallback_owner() {
        let result = NativeBackupRuntime::new(
            FleetLimits {
                streams: 0,
                staging_bytes_per_stream: 0,
                inventory_bytes_per_stream: 0,
                points_per_stream: 0,
            },
            std::iter::empty(),
        );
        assert!(matches!(
            result,
            Err(NativeBackupRuntimeError::Fleet(
                terrabase::backup::worker::FleetError::Configuration
            ))
        ));
    }
}
