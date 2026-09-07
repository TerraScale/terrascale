//! Durable intent for root replacement across process and provider failures.
use super::*;

pub(super) fn read(stream: &Stream) -> Result<Option<(StorageSequence, u64)>, FleetError> {
    use std::io::Read;
    let path = stream.config.staging.join("rotation-pending");
    let file = match std::fs::File::open(path) {
        Ok(file) => file,
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(None),
        Err(_) => return Err(FleetError::Capture),
    };
    let mut text = String::new();
    file.take(129).read_to_string(&mut text).map_err(|_| FleetError::Capture)?;
    if text.len() > 128 {
        return Err(FleetError::Capture);
    }
    let (base, clock) = text.trim_end().split_once(' ').ok_or(FleetError::Capture)?;
    Ok(Some((
        StorageSequence::new(base.parse().map_err(|_| FleetError::Capture)?),
        clock.parse().map_err(|_| FleetError::Capture)?,
    )))
}

pub(super) fn begin(stream: &Stream, base: StorageSequence, clock: u64) -> Result<(), FleetError> {
    use std::io::Write;
    let temporary = stream.config.staging.join("rotation-pending.tmp");
    let mut file = std::fs::File::create(&temporary).map_err(|_| FleetError::Capture)?;
    writeln!(file, "{} {clock}", base.get()).map_err(|_| FleetError::Capture)?;
    file.sync_all().map_err(|_| FleetError::Capture)?;
    std::fs::rename(temporary, stream.config.staging.join("rotation-pending"))
        .map_err(|_| FleetError::Capture)?;
    super::super::sync_directory(&stream.config.staging).map_err(|_| FleetError::Capture)
}

pub(super) fn finish(stream: &Stream) -> Result<(), FleetError> {
    match std::fs::remove_file(stream.config.staging.join("rotation-pending")) {
        Ok(()) => {
            super::super::sync_directory(&stream.config.staging).map_err(|_| FleetError::Capture)
        }
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(()),
        Err(_) => Err(FleetError::Capture),
    }
}
