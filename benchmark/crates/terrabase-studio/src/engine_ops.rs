//! Every engine interaction the studio performs, through the public API only.
//!
//! Nothing here reaches into a private engine module: reads go through
//! [`terrabase::Engine::read_snapshot`] and a borrowed
//! [`terrabase::Transaction`], writes through [`terrabase::Engine::begin`] and
//! `Transaction::commit`, and listing or querying goes through
//! [`terrabase::QueryStore`], which is the public paging surface (the storage
//! port's `documents_at_from` window is crate-private).

use terrabase::query::{OrderDirection, OrderingKey, QueryCondition, StalenessMode};
use terrabase::types::CollectionName;
use terrabase::{
    CommitOutcome, Deadline, Document, DocumentId, DocumentValue, Engine, EngineError,
    ExpectedVersion, LocalDurability, OperationId, QueryInput, QueryOrdering, QueryPaging,
    QueryPredicate, QueryProjection, QueryResult, QuerySource, QuerySpec, ReadSnapshot,
    RequestValidationError, StalenessPolicy, StorageSequence, TenantId, TenantScope, TqlQueryText,
    Transaction,
};

/// Documents per page in the studio's list views.
pub const PAGE_SIZE: u32 = 20;

/// A typed failure of one studio operation, already safe to show.
#[derive(Debug)]
pub struct OpError(pub String);

impl OpError {
    /// Builds the failure from a pre-rendered message.
    pub fn new(message: impl Into<String>) -> Self {
        Self(message.into())
    }
}

impl std::fmt::Display for OpError {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.write_str(&self.0)
    }
}

impl From<OpError> for String {
    fn from(error: OpError) -> Self {
        error.0
    }
}

/// Renders an engine error with the variant named, so nothing is swallowed.
pub fn engine_error(context: &str, error: EngineError) -> OpError {
    OpError(format!("{context}: engine error {error:?}"))
}

/// Renders a request-validation failure.
pub fn validation_error(context: &str, error: RequestValidationError) -> OpError {
    OpError(format!("{context}: invalid value ({error:?})"))
}

/// Builds a validated tenant identifier.
pub fn tenant(name: &str) -> Result<TenantId, OpError> {
    TenantId::try_new(name.trim()).map_err(|error| validation_error("tenant", error))
}

/// Builds a validated collection identifier.
pub fn collection(name: &str) -> Result<CollectionName, OpError> {
    CollectionName::try_new(name.trim()).map_err(|error| validation_error("collection", error))
}

/// Builds a validated document identifier.
pub fn document_id(name: &str) -> Result<DocumentId, OpError> {
    DocumentId::try_new(name.trim()).map_err(|error| validation_error("document id", error))
}

/// The scope and snapshot pair every read and write starts from.
pub struct Session<'engine> {
    /// The tenant scope the engine minted.
    pub scope: TenantScope,
    /// The immutable snapshot this session reads.
    pub snapshot: ReadSnapshot,
    engine: &'engine Engine,
}

/// Opens one tenant's scope and a fresh published snapshot.
pub fn session<'engine>(
    engine: &'engine Engine,
    tenant: &TenantId,
) -> Result<Session<'engine>, OpError> {
    let scope = engine.scope_for(tenant);
    let snapshot = engine
        .read_snapshot(&scope)
        .map_err(|error| engine_error("cannot open a read snapshot", error))?;
    Ok(Session { scope, snapshot, engine })
}

impl Session<'_> {
    /// Begins a transaction bound to a caller-supplied operation identity.
    pub fn begin(&self, operation: OperationId) -> Result<Transaction<'_>, OpError> {
        self.engine
            .begin(&self.scope, self.snapshot.clone(), operation)
            .map_err(|error| engine_error("cannot begin a transaction", error))
    }

    /// Reads one document by exact tenant, collection, and identifier.
    pub fn read(
        &self,
        collection: &CollectionName,
        id: &DocumentId,
    ) -> Result<Option<Document>, OpError> {
        let read_operation = OperationId::try_new("studio-read")
            .map_err(|error| validation_error("operation", error))?;
        let transaction = self.begin(read_operation)?;
        transaction
            .read_in_collection(collection, id)
            .map_err(|error| engine_error("cannot read the document", error))
    }
}

/// One row of a document list view.
pub struct DocumentRow {
    /// The document identifier.
    pub id: String,
    /// The published document version.
    pub version: u64,
    /// Compact JSON of the whole document value.
    pub json: String,
    /// The leading top-level field names, for a readable preview.
    pub fields: Vec<String>,
}

/// A page of documents plus the counts the pager needs.
pub struct DocumentPage {
    /// The rows of this page, in result order.
    pub rows: Vec<DocumentRow>,
    /// How many rows the predicate matched before paging.
    pub matched: u64,
    /// The offset this page starts at.
    pub offset: u64,
    /// The page size that was requested.
    pub limit: u32,
}

impl DocumentPage {
    /// The zero-based index of the current page.
    pub fn number(&self) -> u64 {
        self.offset / u64::from(self.limit.max(1))
    }

    /// The number of pages, at least one so the pager always renders.
    pub fn pages(&self) -> u64 {
        let limit = u64::from(self.limit.max(1));
        self.matched.div_ceil(limit)
    }
}

/// Reads one bounded page of a collection in canonical key order.
///
/// This is the studio's pagination: it compiles a typed specification with an
/// empty predicate, a projection of the whole document, and an offset/limit
/// page, then executes it against a fresh snapshot. `matched` carries the
/// pre-paging count the pager displays.
pub fn list_documents(
    engine: &Engine,
    tenant: &TenantId,
    collection: &CollectionName,
    page: u64,
    limit: u32,
) -> Result<DocumentPage, OpError> {
    let limit = limit.clamp(1, 1024);
    let offset = page.saturating_mul(u64::from(limit));
    let result = run_typed(
        engine,
        tenant,
        QueryForm {
            collection: collection.as_str().to_owned(),
            conditions: Vec::new(),
            order_field: None,
            order_direction: "asc".to_owned(),
            offset,
            limit: Some(limit),
            projection: Vec::new(),
        },
    )?;
    let rows = result
        .rows()
        .iter()
        .map(|row| {
            let document = row.document();
            DocumentRow {
                id: document.id().as_str().to_owned(),
                version: document.metadata().version(),
                json: crate::json::to_compact(document.value()),
                fields: crate::json::preview_fields(document.value(), 4),
            }
        })
        .collect();
    Ok(DocumentPage { rows, matched: result.matched(), offset, limit })
}

/// The typed query form the guided query page submits.
#[derive(Clone, Debug, Default)]
pub struct QueryForm {
    /// The collection to read.
    pub collection: String,
    /// Equality, comparison, membership, and pattern conditions.
    pub conditions: Vec<ConditionForm>,
    /// The ordering field, or `@id` for canonical key order.
    pub order_field: Option<String>,
    /// `asc` or `desc`.
    pub order_direction: String,
    /// The page offset.
    pub offset: u64,
    /// The page limit, if any.
    pub limit: Option<u32>,
    /// Projection field names; empty selects the whole document.
    pub projection: Vec<String>,
}

/// One condition row of the guided query form.
#[derive(Clone, Debug)]
pub struct ConditionForm {
    /// The field the condition names.
    pub field: String,
    /// The condition kind, as the form names it.
    pub kind: String,
    /// The comparison value as JSON text, when the kind takes one.
    pub value: String,
    /// The high bound of a `between` condition as JSON text.
    pub high: String,
}

/// Parses one condition value from JSON text into a structured value.
fn parse_value(text: &str) -> Result<DocumentValue, OpError> {
    let parsed: serde_json::Value = serde_json::from_str(text)
        .map_err(|error| OpError(format!("`{text}` is not valid JSON: {error}")))?;
    Ok(crate::json::from_json(parsed))
}

/// Compiles and executes one typed query.
///
/// The specification is built with the engine's own validating constructors,
/// so an operator cannot submit a shape the engine would refuse later: every
/// malformed field, over-long ordering, or bad page bound reports here with
/// the engine's typed validation failure.
pub fn run_typed(
    engine: &Engine,
    tenant: &TenantId,
    form: QueryForm,
) -> Result<QueryResult, OpError> {
    let source = QuerySource::try_new(collection(&form.collection)?)
        .map_err(|error| validation_error("collection", error))?;
    let mut conditions = Vec::new();
    for condition in &form.conditions {
        let field = condition.field.trim().to_owned();
        if field.is_empty() {
            continue;
        }
        let built = match condition.kind.as_str() {
            "equals" => QueryCondition::Equals { field, value: parse_value(&condition.value)? },
            "not-equals" => {
                QueryCondition::NotEquals { field, value: parse_value(&condition.value)? }
            }
            "greater" => {
                QueryCondition::GreaterThan { field, value: parse_value(&condition.value)? }
            }
            "greater-or-equal" => {
                QueryCondition::GreaterOrEqual { field, value: parse_value(&condition.value)? }
            }
            "less" => QueryCondition::LessThan { field, value: parse_value(&condition.value)? },
            "less-or-equal" => {
                QueryCondition::LessOrEqual { field, value: parse_value(&condition.value)? }
            }
            "between" => QueryCondition::Between {
                field,
                low: parse_value(&condition.value)?,
                high: parse_value(&condition.high)?,
            },
            "in" => {
                let parsed: serde_json::Value = serde_json::from_str(&condition.value)
                    .map_err(|error| OpError(format!("`in` needs a JSON array: {error}")))?;
                let serde_json::Value::Array(items) = parsed else {
                    return Err(OpError::new("`in` needs a JSON array"));
                };
                QueryCondition::In {
                    field,
                    values: items.into_iter().map(crate::json::from_json).collect(),
                }
            }
            "starts-with" => {
                QueryCondition::StartsWith { field, value: condition.value.trim().to_owned() }
            }
            "ends-with" => {
                QueryCondition::EndsWith { field, value: condition.value.trim().to_owned() }
            }
            "contains" => {
                QueryCondition::Contains { field, value: condition.value.trim().to_owned() }
            }
            "exists" => QueryCondition::Exists { field },
            "missing" => QueryCondition::Missing { field },
            other => return Err(OpError(format!("unsupported condition `{other}`"))),
        };
        conditions.push(built);
    }
    let predicate =
        QueryPredicate::all(conditions).map_err(|error| validation_error("predicate", error))?;
    // No ordering keys is the engine's natural (canonical key) order, which is
    // what a document list wants; a named field adds one validated key.
    let ordering = match form.order_field.as_deref().map(str::trim).filter(|text| !text.is_empty())
    {
        None => QueryOrdering::default(),
        Some(field) => {
            let direction = if form.order_direction == "desc" {
                OrderDirection::Descending
            } else {
                OrderDirection::Ascending
            };
            let key = OrderingKey::try_new(field, direction)
                .map_err(|error| validation_error("ordering", error))?;
            QueryOrdering::try_new(vec![key])
                .map_err(|error| validation_error("ordering", error))?
        }
    };
    let projection = if form.projection.is_empty() {
        QueryProjection::whole_document()
    } else {
        let trimmed: Vec<String> = form
            .projection
            .iter()
            .map(|text| text.trim().to_owned())
            .filter(|text| !text.is_empty())
            .collect();
        let names: Vec<&str> = trimmed.iter().map(String::as_str).collect();
        QueryProjection::fields(&names).map_err(|error| validation_error("projection", error))?
    };
    let paging = QueryPaging::try_new(form.offset, form.limit)
        .map_err(|error| validation_error("paging", error))?;
    // Strict with an empty namespace list: the engine's snapshots carry an
    // empty observed-generation set, so naming an index under `Strict` would
    // refuse with `IndexNotReady`. An empty list is the index-less scan the
    // studio's guided query wants, and it lets the optimizer pick an auto
    // index on its own.
    let staleness = StalenessPolicy::try_new(StalenessMode::Strict, Vec::new())
        .map_err(|error| validation_error("staleness", error))?;
    let deadline = Deadline::after(std::time::Duration::from_secs(30))
        .map_err(|error| validation_error("deadline", error))?;
    let spec = QuerySpec::try_new(
        tenant.clone(),
        source,
        predicate,
        ordering,
        projection,
        paging,
        staleness,
        deadline,
        terrabase::Cancellation::new(),
    )
    .map_err(|error| validation_error("query", error))?;
    let session = session(engine, tenant)?;
    let store = engine.queries();
    let plan = store
        .compile(QueryInput::Typed(spec))
        .map_err(|error| OpError(format!("the query does not compile: {error:?}")))?;
    store
        .execute(&plan, &session.snapshot)
        .map_err(|error| OpError(format!("query failed: {error:?}")))
}

/// Compiles and executes bounded TQL text against one tenant snapshot.
pub fn run_tql(engine: &Engine, tenant: &TenantId, text: &str) -> Result<QueryResult, OpError> {
    let text = TqlQueryText::try_new(text)
        .map_err(|error| OpError(format!("TQL input refused: {error:?}")))?;
    let deadline = Deadline::after(std::time::Duration::from_secs(30))
        .map_err(|error| validation_error("deadline", error))?;
    let store = engine.queries();
    let plan = store
        .compile(QueryInput::tql_bounded(text, deadline, terrabase::Cancellation::new()))
        .map_err(|error| OpError(format!("TQL compilation failed: {error:?}")))?;
    let session = session(engine, tenant)?;
    store
        .execute(&plan, &session.snapshot)
        .map_err(|error| OpError(format!("TQL execution failed: {error:?}")))
}

/// Writes one document and returns the local commit receipt.
pub fn put_document(
    engine: &Engine,
    operation: OperationId,
    tenant: &TenantId,
    collection: &CollectionName,
    id: &DocumentId,
    value: DocumentValue,
    expected: ExpectedVersion,
) -> Result<CommitOutcome, OpError> {
    let session = session(engine, tenant)?;
    let mut transaction = session.begin(operation)?;
    let document = Document::new(id.clone(), collection.clone(), value);
    transaction
        .put(document, expected)
        .map_err(|error| engine_error("the document was rejected", error))?;
    let durability = LocalDurability::Synced;
    Ok(transaction.commit(durability))
}

/// Deletes one document by exact key and returns the local commit outcome.
pub fn delete_document(
    engine: &Engine,
    operation: OperationId,
    tenant: &TenantId,
    collection: &CollectionName,
    id: &DocumentId,
    expected: ExpectedVersion,
) -> Result<CommitOutcome, OpError> {
    let session = session(engine, tenant)?;
    let mut transaction = session.begin(operation)?;
    transaction.delete_in_collection(collection, id.clone(), expected);
    Ok(transaction.commit(LocalDurability::Synced))
}

/// Parses the editor's expected-version choice.
pub fn expected_version(choice: &str, exact: &str) -> Result<ExpectedVersion, OpError> {
    match choice {
        "any" => Ok(ExpectedVersion::Any),
        "new" => Ok(ExpectedVersion::New),
        "exact" => exact
            .trim()
            .parse::<u64>()
            .map(ExpectedVersion::Exact)
            .map_err(|error| OpError(format!("`{exact}` is not a version number: {error}"))),
        other => Err(OpError(format!("unsupported expected-version choice `{other}`"))),
    }
}

/// Runs one compaction pass and returns the typed receipt.
pub fn compact(
    engine: &Engine,
    request: terrabase::CompactionRequest,
) -> Result<terrabase::CompactionReceipt, OpError> {
    engine.compact(request).map_err(|error| OpError(format!("compaction refused: {error:?}")))
}

/// The published sequence of the engine location.
pub fn published_sequence(engine: &Engine) -> Result<StorageSequence, OpError> {
    engine
        .published_sequence()
        .map_err(|error| engine_error("cannot read the published sequence", error))
}

#[cfg(test)]
mod tests {
    use std::sync::atomic::{AtomicU64, Ordering};

    use super::*;

    fn scratch(name: &str) -> Engine {
        let directory = std::env::temp_dir().join(format!(
            "terrabase-studio-ops-{name}-{}-{}",
            std::process::id(),
            std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos()
        ));
        let config =
            terrabase::EngineConfig::durable(directory).expect("scratch location is usable");
        Engine::open(config).expect("scratch engine opens")
    }

    fn write_one(
        engine: &Engine,
        tenant_name: &str,
        collection_name: &str,
        id: &str,
        body: serde_json::Value,
    ) -> CommitOutcome {
        let tenant = tenant(tenant_name).unwrap();
        let collection = collection(collection_name).unwrap();
        let id = document_id(id).unwrap();
        static WRITES: AtomicU64 = AtomicU64::new(0);
        let sequence = WRITES.fetch_add(1, Ordering::Relaxed);
        let operation = OperationId::try_new(&format!("test-{id}-{sequence}")).unwrap();
        let outcome = put_document(
            engine,
            operation,
            &tenant,
            &collection,
            &id,
            crate::json::from_json(body),
            ExpectedVersion::Any,
        )
        .unwrap();
        assert!(
            matches!(outcome, CommitOutcome::Committed(_)),
            "the sample write was refused: {outcome:?}"
        );
        outcome
    }

    #[test]
    fn pagination_walks_the_collection_in_fixed_pages() {
        let engine = scratch("paging");
        let tenant = tenant("paging").unwrap();
        let collection = collection("users").unwrap();
        for index in 0..45 {
            let outcome = write_one(
                &engine,
                "paging",
                "users",
                &format!("user-{index:03}"),
                serde_json::json!({"index": index}),
            );
            assert!(matches!(outcome, CommitOutcome::Committed(_)), "write {index} committed");
        }

        let first = list_documents(&engine, &tenant, &collection, 0, PAGE_SIZE).unwrap();
        assert_eq!(first.rows.len(), PAGE_SIZE as usize);
        assert_eq!(first.matched, 45);
        assert_eq!(first.pages(), 3);
        assert_eq!(first.number(), 0);
        assert_eq!(first.rows[0].id, "user-000");
        assert_eq!(first.rows[19].id, "user-019");

        let last = list_documents(&engine, &tenant, &collection, 2, PAGE_SIZE).unwrap();
        assert_eq!(last.rows.len(), 5);
        assert_eq!(last.rows[0].id, "user-040");
        assert_eq!(last.number(), 2);

        let beyond = list_documents(&engine, &tenant, &collection, 9, PAGE_SIZE).unwrap();
        assert!(beyond.rows.is_empty());
        assert_eq!(beyond.matched, 45);
    }

    #[test]
    fn page_size_is_clamped_to_the_engine_bound() {
        let engine = scratch("clamp");
        let tenant = tenant("clamp").unwrap();
        let collection = collection("users").unwrap();
        write_one(&engine, "clamp", "users", "one", serde_json::json!({}));
        let page = list_documents(&engine, &tenant, &collection, 0, 9_999).unwrap();
        assert_eq!(page.limit, 1024);
        assert_eq!(page.rows.len(), 1);
    }

    #[test]
    fn a_point_read_resolves_the_exact_collection_copy() {
        let engine = scratch("point-read");
        let tenant = tenant("point").unwrap();
        let a = collection("a").unwrap();
        let b = collection("b").unwrap();
        let id = document_id("shared").unwrap();
        write_one(&engine, "point", "a", "shared", serde_json::json!({"where": "a"}));
        write_one(&engine, "point", "b", "shared", serde_json::json!({"where": "b"}));

        let session = session(&engine, &tenant).unwrap();
        let in_a = session.read(&a, &id).unwrap().expect("present in a");
        let in_b = session.read(&b, &id).unwrap().expect("present in b");
        assert_eq!(crate::json::to_compact(in_a.value()), r#"{"where":"a"}"#);
        assert_eq!(crate::json::to_compact(in_b.value()), r#"{"where":"b"}"#);
        assert!(session.read(&a, &document_id("absent").unwrap()).unwrap().is_none());
    }

    #[test]
    fn a_typed_condition_selects_only_matching_rows() {
        let engine = scratch("conditions");
        for (id, level) in [("u1", 3), ("u2", 9), ("u3", 12)] {
            write_one(&engine, "cond", "users", id, serde_json::json!({"level": level}));
        }
        let result = run_typed(
            &engine,
            &tenant("cond").unwrap(),
            QueryForm {
                collection: "users".to_owned(),
                conditions: vec![ConditionForm {
                    field: "level".to_owned(),
                    kind: "greater".to_owned(),
                    value: "5".to_owned(),
                    high: String::new(),
                }],
                order_field: None,
                order_direction: "asc".to_owned(),
                offset: 0,
                limit: Some(10),
                projection: Vec::new(),
            },
        )
        .unwrap();
        let ids: Vec<&str> = result.rows().iter().map(|row| row.document().id().as_str()).collect();
        assert_eq!(ids, vec!["u2", "u3"]);
        assert_eq!(result.matched(), 2);
    }

    #[test]
    fn bounded_tql_executes_against_the_selected_tenant_snapshot() {
        let engine = scratch("tql");
        for (id, level) in [("u1", 3), ("u2", 9), ("u3", 12)] {
            write_one(&engine, "tql", "users", id, serde_json::json!({"level": level}));
        }
        let result = run_tql(
            &engine,
            &tenant("tql").unwrap(),
            "from users where level > 5 order by level limit 10",
        )
        .unwrap();
        let ids = result.rows().iter().map(|row| row.document().id().as_str()).collect::<Vec<_>>();
        assert_eq!(ids, vec!["u2", "u3"]);
    }

    #[test]
    fn an_unparsable_condition_value_reports_a_typed_failure() {
        let engine = scratch("bad-value");
        write_one(&engine, "bad", "users", "u1", serde_json::json!({"a": 1}));
        let error = run_typed(
            &engine,
            &tenant("bad").unwrap(),
            QueryForm {
                collection: "users".to_owned(),
                conditions: vec![ConditionForm {
                    field: "a".to_owned(),
                    kind: "equals".to_owned(),
                    value: "{not json".to_owned(),
                    high: String::new(),
                }],
                order_field: None,
                order_direction: String::new(),
                offset: 0,
                limit: Some(10),
                projection: Vec::new(),
            },
        )
        .unwrap_err();
        assert!(error.0.contains("not valid JSON"), "got: {}", error.0);
    }

    #[test]
    fn delete_removes_the_document_and_reports_the_sequence() {
        let engine = scratch("delete");
        let tenant = tenant("del").unwrap();
        let collection = collection("users").unwrap();
        write_one(&engine, "del", "users", "gone", serde_json::json!({"a": 1}));
        let operation = OperationId::try_new("test-delete").unwrap();
        let outcome = delete_document(
            &engine,
            operation,
            &tenant,
            &collection,
            &document_id("gone").unwrap(),
            ExpectedVersion::Any,
        )
        .unwrap();
        assert!(matches!(outcome, CommitOutcome::Committed(_)));
        let page = list_documents(&engine, &tenant, &collection, 0, PAGE_SIZE).unwrap();
        assert_eq!(page.matched, 0);
    }

    #[test]
    fn expected_version_choices_parse() {
        assert_eq!(expected_version("any", "").unwrap(), ExpectedVersion::Any);
        assert_eq!(expected_version("new", "").unwrap(), ExpectedVersion::New);
        assert_eq!(expected_version("exact", "7").unwrap(), ExpectedVersion::Exact(7));
        assert!(expected_version("exact", "seven").is_err());
        assert!(expected_version("sometimes", "").is_err());
    }
}
