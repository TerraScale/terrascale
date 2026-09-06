//! Complete retained publication transfer for in-process engine composition.
//! Production socket traffic uses `production_causal` and its RF2 receipt gate.
use crate::{engine_host::EngineHost, host::HostingError, registry::BoundScope};
use terrabase::engine::ReplicatedPublication;
use terrabase::types::Cancellation;
fn failure(error: impl std::fmt::Debug) -> HostingError {
    HostingError::EngineFailure(format!("publication transfer: {error:?}"))
}

/// Reads one bounded page of complete locally originated transactions.
/// # Errors
/// Returns durable scope/storage errors without advancing the caller cursor.
pub fn pull_origin_publications(
    host: &EngineHost,
    scope: &BoundScope,
    after: Option<u64>,
) -> Result<(Vec<ReplicatedPublication>, Option<u64>), HostingError> {
    let bound = host.engine_scope_of(scope)?;
    let origin = host.engine().replication_origin().map_err(failure)?;
    let rows = host
        .engine()
        .replication_publications(&bound, after.unwrap_or(0), 1024)
        .map_err(failure)?;
    let next = rows.last().map(|(sequence, _)| *sequence);
    Ok((
        rows.into_iter()
            .filter_map(|(_, publication)| (publication.origin == origin).then_some(publication))
            .collect(),
        next,
    ))
}
/// Atomically applies each complete source transaction; replay counts as zero.
/// # Errors
/// Refuses missing causal dependencies or failed complete-group publication.
pub fn apply_publications(
    host: &EngineHost,
    scope: &BoundScope,
    publications: &[ReplicatedPublication],
) -> Result<usize, HostingError> {
    let bound = host.engine_scope_of(scope)?;
    let mut count = 0;
    for publication in publications {
        if !host
            .engine()
            .replication_publication_dependencies_ready(&bound, publication)
            .map_err(failure)?
        {
            return Err(HostingError::Refused(
                "publication dependencies need reconciliation".into(),
            ));
        }
        let outcome =
            host.engine().apply_replicated_publication(&bound, publication).map_err(failure)?;
        if !outcome.replayed() {
            count += publication.records.len().max(1);
        }
    }
    Ok(count)
}
/// Transfers every complete local transaction beyond the acknowledged cursor.
/// # Errors
/// Refuses source/apply failures without advancing past an incomplete page.
pub fn replicate_once(
    source: &EngineHost,
    target: &EngineHost,
    scope: &BoundScope,
    cursor: &mut Option<u64>,
) -> Result<usize, HostingError> {
    replicate_once_with_cancellation(source, target, scope, cursor, &Cancellation::default())
}
/// Transfers complete transactions with cancellation at each bounded page.
/// # Errors
/// Returns cancellation or source/apply failures, preserving retry-safe progress.
pub fn replicate_once_with_cancellation(
    source: &EngineHost,
    target: &EngineHost,
    scope: &BoundScope,
    cursor: &mut Option<u64>,
    cancellation: &Cancellation,
) -> Result<usize, HostingError> {
    let mut count = 0;
    loop {
        if cancellation.is_cancelled() {
            return Err(HostingError::Refused("publication transfer cancelled".into()));
        }
        let (publications, next) = pull_origin_publications(source, scope, *cursor)?;
        count += apply_publications(target, scope, &publications)?;
        let Some(next) = next else { return Ok(count) };
        if cursor.is_some_and(|previous| next <= previous) {
            return Err(HostingError::Refused("publication cursor did not advance".into()));
        }
        *cursor = Some(next);
    }
}
