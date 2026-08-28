//! The family-source execution bridge (GAP-Q19).
//!
//! A counter or time-series source executes against the same immutable
//! snapshot the documents path executes against, but its row space is the
//! family entry space, and every value comes from the family seam's own
//! readers: the counter source reads each document's counter group through
//! [`FamilyReadRequest::CounterGroup`], and the time-series source reads
//! each `(document, series)` through [`FamilyReadRequest::TimeSeriesRange`]
//! with the query's bound window — the GAP-F04 read contract, cursor-stable
//! paging included. The module coordinates those reads and never re-derives
//! family state, exactly like the includes contract beside it: a query row,
//! an include entry, and a direct family read can never disagree.
//!
//! The contract record — the source-type vocabulary, the per-family row
//! semantics, and the limits — is `docs/query/gapq19-family-sources.md`.

use std::collections::BTreeMap;
use std::time::Instant;

use super::QueryError;
use super::includes::{map_family_error, read_cancellation, read_deadline, read_family};
use super::model::{self, NormalizedQuery};
use super::result::{
    CounterQueryRow, FamilyQueryRow, QueryResult, SeriesPageCursor, TimeSeriesQueryRow,
};
use super::spec::QuerySourceKind;
use super::statistics::{DiagnosticDuration, QueryStatistics, ResultEtag};
use crate::families::{
    CounterRead, FamilyError, FamilyReadRequest, FamilyReadResult, TimeSeriesEntry, TimeSeriesRead,
};
use crate::types::{DocumentId, DocumentValue, ReadSnapshot};
use crate::{engine::Engine, families::RESERVED_COLLECTION, types::TenantScope};

/// How many enumerated entries pass between two bounded checkpoints, the
/// same interval the documents path applies.
const CHECKPOINT_INTERVAL: usize = 64;

/// The field space of one family row: the closed per-family vocabulary the
/// predicate, ordering, and projection name. See the decision record D4 for
/// the V1 anchors (`CounterEntry` and `TimeSeriesEntry` field names).
type EntryFields = BTreeMap<&'static str, DocumentValue>;

/// One enumerated family row: its typed value and its field map, carried
/// together so filtering and ordering evaluate the same fields the typed
/// accessors expose.
struct EnumeratedRow {
    fields: EntryFields,
    row: FamilyQueryRow,
}

/// Executes one family-source plan against one immutable snapshot.
///
/// The caller has already validated the tenant claim, the (empty) namespace
/// set, and the bounded checkpoints; this path enumerates the family entry
/// space through the family read seam, filters it with the plan's predicate
/// over the entry field space, orders it with the plan's ordering under the
/// seam's total document order, and pages it with the plan's page request.
/// The result carries the typed family rows, the per-series resume cursors
/// of a time-series source, and the same FILTER accounting the documents
/// path records over its scan.
pub(crate) fn execute_family(
    engine: &Engine,
    query: &NormalizedQuery,
    snapshot: &ReadSnapshot,
    staleness: super::result::StalenessReport,
) -> Result<QueryResult, QueryError> {
    let started = Instant::now();
    let scope = TenantScope::from_parts(snapshot.tenant().clone(), snapshot.engine_identity());
    let documents = engine
        .snapshot_documents(&scope, snapshot)
        .map_err(|error| super::execute::map_engine_error(&error))?;

    let mut enumerated: Vec<EnumeratedRow> = Vec::new();
    let mut cursors: Vec<SeriesPageCursor> = Vec::new();
    match query.source().kind() {
        QuerySourceKind::Counters => {
            enumerate_counters(engine, query, snapshot, &documents, &mut enumerated)?;
        }
        QuerySourceKind::TimeSeries => {
            enumerate_series(engine, query, snapshot, &documents, &mut enumerated, &mut cursors)?;
        }
        // The documents source runs the documents path; the caller routes it
        // before reaching this bridge.
        QuerySourceKind::Collection => return Err(QueryError::InvalidSpec),
    }

    // The same scan accounting the documents path applies, over the family
    // entry space: scanned counts every enumerated entry, skipped the ones
    // the predicate filtered, and the counts saturate.
    let mut matched_rows: Vec<EnumeratedRow> = Vec::new();
    let mut scanned: u64 = 0;
    let mut skipped: u64 = 0;
    for (position, row) in enumerated.into_iter().enumerate() {
        if position % CHECKPOINT_INTERVAL == 0 {
            super::execute::check_bounds(query.deadline(), query.cancellation_signal())?;
        }
        scanned = scanned.saturating_add(1);
        if model::matches_fields(query.predicate(), &row.fields) {
            matched_rows.push(row);
        } else {
            skipped = skipped.saturating_add(1);
        }
    }
    super::execute::check_bounds(query.deadline(), query.cancellation_signal())?;
    matched_rows.sort_by(|left, right| {
        model::compare_field_maps(query.ordering(), &left.fields, &right.fields)
    });
    let matched = u64::try_from(matched_rows.len()).unwrap_or(u64::MAX);

    // The same page window the documents path applies, over the ordered
    // entry rows.
    let offset = usize::try_from(query.paging().offset()).unwrap_or(usize::MAX);
    let limit = query
        .paging()
        .limit()
        .map_or(usize::MAX, |limit| usize::try_from(limit).unwrap_or(usize::MAX));
    let family_rows: Vec<FamilyQueryRow> = matched_rows
        .into_iter()
        .enumerate()
        .filter_map(|(position, row)| {
            (position >= offset && position < offset.saturating_add(limit)).then_some(row.row)
        })
        .collect();

    let statistics = QueryStatistics::new(
        matched,
        query.paging(),
        scanned,
        skipped,
        ResultEtag::of(snapshot.sequence(), &staleness),
        DiagnosticDuration::new(started.elapsed()),
    );
    Ok(QueryResult::from_family_parts(
        family_rows,
        cursors,
        matched,
        query.ordering().clone(),
        staleness,
        statistics,
    ))
}

/// Enumerates the counter entry space of the tenant.
///
/// One counter-group read per user document of the snapshot, in canonical
/// key order — the same order the documents scan observes. A document that
/// no longer exists is never enumerated here (it is not in the view); a
/// counter that does not exist or is tombstoned contributes no row, the
/// read seam's own live-name rule. A named source reads exactly that name
/// per document (V1 `counters.{name}`); the name-less form reads the whole
/// group (V1 `counters`).
fn enumerate_counters(
    engine: &Engine,
    query: &NormalizedQuery,
    snapshot: &ReadSnapshot,
    documents: &[crate::types::Document],
    rows: &mut Vec<EnumeratedRow>,
) -> Result<(), QueryError> {
    let names: Vec<String> =
        query.source().counter_name().map(|name| vec![name.to_owned()]).unwrap_or_default();
    for document in documents {
        if document.collection().as_str() == RESERVED_COLLECTION {
            continue;
        }
        super::execute::check_bounds(query.deadline(), query.cancellation_signal())?;
        let read = CounterRead::try_new(
            document.id().clone(),
            names.clone(),
            read_deadline(query.deadline()),
            read_cancellation(query.cancellation_signal()),
        )
        .map_err(|_| QueryError::StorageFailure)?;
        let group = match read_family(engine, snapshot, FamilyReadRequest::counter_group(read)) {
            Ok(FamilyReadResult::CounterGroup(group)) => group,
            // A family record for a document the view no longer carries has
            // no live entries to enumerate; the read seam's own rule.
            Err(FamilyError::DocumentMissing) => continue,
            Err(error) => return Err(map_family_error(error)),
            Ok(_) => return Err(QueryError::StorageFailure),
        };
        for entry in group.entries() {
            if let Some(value) = entry.value() {
                rows.push(counter_row(document.id().clone(), entry.name().to_owned(), value));
            }
        }
    }
    Ok(())
}

/// Enumerates the time-series entry space of the tenant.
///
/// Series are discovered from the snapshot's reserved series records —
/// `family:time-series/<document>/<name>`, parsed by the families module
/// that owns the reserved layout — filtered by the source's name
/// restriction. Each discovered series is read through the family read seam
/// with the query's bound window: the GAP-F04 read contract, so the served
/// page, its pre-paging total, and its resume cursor are the family seam's
/// own values, and the cursor is reported per series on the result.
fn enumerate_series(
    engine: &Engine,
    query: &NormalizedQuery,
    snapshot: &ReadSnapshot,
    documents: &[crate::types::Document],
    rows: &mut Vec<EnumeratedRow>,
    cursors: &mut Vec<SeriesPageCursor>,
) -> Result<(), QueryError> {
    let (range, paging) = query.time_series_window().unwrap_or((None, None));
    for document in documents {
        if document.collection().as_str() != RESERVED_COLLECTION {
            continue;
        }
        let Some((owner, name)) = crate::families::time_series_record_owner(document.id()) else {
            continue;
        };
        if let Some(restricted) = query.source().series_name()
            && restricted != name
        {
            continue;
        }
        super::execute::check_bounds(query.deadline(), query.cancellation_signal())?;
        let owner_id = DocumentId::try_new(owner).map_err(|_| QueryError::StorageFailure)?;
        let mut read = TimeSeriesRead::try_new(
            owner_id.clone(),
            name.to_owned(),
            range,
            read_deadline(query.deadline()),
            read_cancellation(query.cancellation_signal()),
        )
        .map_err(|_| QueryError::StorageFailure)?;
        if let Some(paging) = paging {
            read = read.with_paging(paging);
        }
        let page = match read_family(engine, snapshot, FamilyReadRequest::time_series_range(read)) {
            Ok(FamilyReadResult::TimeSeriesPage(page)) => page,
            Err(FamilyError::DocumentMissing) => continue,
            Err(error) => return Err(map_family_error(error)),
            Ok(_) => return Err(QueryError::StorageFailure),
        };
        for entry in page.entries() {
            rows.push(series_row(owner_id.clone(), name.to_owned(), entry.clone()));
        }
        cursors.push(SeriesPageCursor::new(owner_id, name.to_owned(), page.next_cursor()));
    }
    Ok(())
}

/// Builds one enumerated counter row with its field map.
fn counter_row(document: DocumentId, name: String, value: i64) -> EnumeratedRow {
    let mut fields = BTreeMap::new();
    fields.insert("document", DocumentValue::String(document.as_str().to_owned()));
    fields.insert("name", DocumentValue::String(name.clone()));
    fields.insert("value", DocumentValue::Integer(value));
    EnumeratedRow {
        fields,
        row: FamilyQueryRow::Counter(CounterQueryRow::new(document, name, value)),
    }
}

/// Builds one enumerated series row with its field map.
fn series_row(document: DocumentId, name: String, entry: TimeSeriesEntry) -> EnumeratedRow {
    let mut fields = BTreeMap::new();
    fields.insert("document", DocumentValue::String(document.as_str().to_owned()));
    fields.insert("name", DocumentValue::String(name.clone()));
    fields.insert("timestamp", DocumentValue::Integer(entry.timestamp()));
    fields.insert(
        "values",
        DocumentValue::Array(
            entry.values().iter().map(|value| DocumentValue::Number(*value)).collect(),
        ),
    );
    match entry.tag() {
        Some(tag) => fields.insert("tag", DocumentValue::String(tag.to_owned())),
        None => fields.insert("tag", DocumentValue::Null),
    };
    EnumeratedRow {
        fields,
        row: FamilyQueryRow::TimeSeries(TimeSeriesQueryRow::new(document, name, entry)),
    }
}
