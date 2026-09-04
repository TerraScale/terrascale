//! Candidate generation and the SQLite hypothetical-compile seam.
//!
//! Candidate shape is deliberately kept in the compatibility module.  This
//! file owns the production boundary around that shape: inputs are bounded
//! before they reach the generator and the planner check runs only against a
//! private in-memory schema.  In particular, no connection supplied by the
//! serving engine is ever used here, so a what-if index cannot become visible
//! in a production schema.

use std::collections::{BTreeMap, BTreeSet};

use rusqlite::Connection;

use super::{
    Candidate, CandidateColumn, CandidateKind, CandidateSource, ColumnRole, ColumnSketch,
    PredicateKind, Template, WeightedTemplate, equality_selectivity, estimated_width_bytes,
};

/// The maximum number of candidates one collection may retain.
pub(crate) const MAX_CANDIDATES: usize = super::MAX_CANDIDATES_PER_COLLECTION;

/// The maximum number of fields a hypothetical candidate may contain.
pub(crate) const MAX_CANDIDATE_FIELDS: usize = super::super::MAX_DEFINITION_FIELDS;

/// The scratch table may contain fields from predicates, ordering, and the
/// projection.  Each input is bounded by the candidate field limit, so three
/// bounded groups are the largest statement the compiler will construct.
const MAX_HYPOTHETICAL_FIELDS: usize = MAX_CANDIDATE_FIELDS.saturating_mul(3);

/// Fixed row count used to give SQLite a useful, deterministic cardinality
/// without copying production data into the what-if connection.
const HYPOTHETICAL_ROWS: usize = 256;

/// Refuse unexpectedly large generated SQL before passing it to SQLite.
const MAX_HYPOTHETICAL_SQL_BYTES: usize = 128 * 1024;

/// Why candidate generation or hypothetical compilation refused to proceed.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum CandidateError {
    /// A collection, predicate, ordering, projection, or candidate field is
    /// not a valid engine identifier.
    InvalidIdentifier,
    /// The input exceeded one of the fixed candidate bounds.
    BoundExceeded,
    /// Schema versions are positive and the compiler's version must match.
    InvalidSchemaVersion,
    /// The candidate kind has no supported scratch-schema representation.
    UnsupportedCandidate,
    /// SQLite produced a planner row this adapter does not recognize.
    UnknownPlannerOutput,
    /// The scratch schema or EXPLAIN statement failed.
    ScratchSchemaFailure,
}

/// Result of asking SQLite whether a candidate changes the chosen plan.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub(crate) enum CompileDecision {
    /// The hypothetical index is selected by the planner.
    Used,
    /// The planner keeps the existing plan.
    #[default]
    NotUsed,
}

/// Port implemented by the SQLite planner integration.
pub(crate) trait HypotheticalCompiler {
    /// Plans `template` against `candidate` without mutating the live schema.
    fn compile(&mut self, template: &Template, candidate: &Candidate) -> CompileDecision;
}

/// Generates the deterministic candidates for one weighted template.
///
/// Applies the frozen order: equality prefix, one range field, ordering keys,
/// and an optional covering tail. Every emitted field must already be
/// extracted; descriptor creation belongs to the write/schema path. Bounds
/// are checked before and after construction, and no input is truncated.
pub(crate) fn try_generate(
    template: &WeightedTemplate,
    sketches: &BTreeMap<(String, String), ColumnSketch>,
    extracted: &BTreeSet<String>,
) -> Result<Vec<Candidate>, CandidateError> {
    validate_template(template.template())?;
    let inner = template.template();

    // Candidates are executable only over STORED/extracted columns.  A
    // missing descriptor is a separate write-path operation and must never be
    // smuggled into an index proposal by this read-side generator.
    if inner.has_full_text() {
        let Some(field) = inner
            .predicates()
            .iter()
            .find(|predicate| predicate.kind() == PredicateKind::FullText)
            .map(|predicate| predicate.field())
        else {
            return Ok(Vec::new());
        };
        if !extracted.contains(field) {
            return Ok(Vec::new());
        }
        let column = CandidateColumn::try_new(field, ColumnRole::Equality)
            .map_err(|_| CandidateError::InvalidIdentifier)?;
        return Ok(vec![Candidate {
            collection: inner.collection().to_owned(),
            columns: vec![column],
            kind: CandidateKind::Fts,
            source: CandidateSource::Workload,
            needs_descriptor: false,
            benefit_per_byte: template.weight().max(1.0),
        }]);
    }

    let mut columns = Vec::new();
    let mut seen = BTreeSet::new();
    for predicate in inner.predicates() {
        if predicate.kind() != PredicateKind::Equality
            || !extracted.contains(predicate.field())
            || !seen.insert(predicate.field().to_owned())
        {
            continue;
        }
        columns.push(candidate_column(predicate.field(), ColumnRole::Equality)?);
    }
    if let Some(predicate) = inner.predicates().iter().find(|predicate| {
        predicate.kind() == PredicateKind::Range
            && extracted.contains(predicate.field())
            && !seen.contains(predicate.field())
    }) {
        seen.insert(predicate.field().to_owned());
        columns.push(candidate_column(predicate.field(), ColumnRole::Range)?);
    }
    for field in inner.ordering() {
        if extracted.contains(field) && seen.insert(field.clone()) {
            columns.push(candidate_column(field, ColumnRole::Order)?);
        }
    }
    if columns.is_empty() {
        return Ok(Vec::new());
    }
    if columns.len() > MAX_CANDIDATE_FIELDS {
        return Err(CandidateError::BoundExceeded);
    }

    let width = estimated_width_bytes(&columns).max(1);
    let prior = columns
        .iter()
        .filter(|column| column.role() == ColumnRole::Equality)
        .map(|column| {
            equality_selectivity(
                sketches,
                inner.collection(),
                column.field(),
                &crate::types::DocumentValue::Null,
            )
        })
        .fold(1.0, |acc, selectivity| acc * (1.0 - selectivity * 0.5));
    let base = Candidate {
        collection: inner.collection().to_owned(),
        columns: columns.clone(),
        kind: CandidateKind::BTree,
        source: CandidateSource::Workload,
        needs_descriptor: false,
        benefit_per_byte: (template.weight() * prior / width as f64).max(0.0),
    };
    let mut candidates = vec![base];

    // The covering form appends only extracted projected fields.  Projection
    // order is already canonicalized by Template::try_new; retaining it here
    // makes output stable across runs and keeps the classic prefix intact.
    if let Some(projection) = inner.projection() {
        let mut covering = columns;
        for field in projection {
            if seen.contains(field) || !extracted.contains(field) {
                continue;
            }
            covering.push(candidate_column(field, ColumnRole::Covering)?);
            seen.insert(field.clone());
        }
        if covering.len() > MAX_CANDIDATE_FIELDS {
            return Err(CandidateError::BoundExceeded);
        }
        let covering_width = estimated_width_bytes(&covering).max(1);
        if covering.len() > candidates[0].columns.len() && covering_width <= width.saturating_mul(2)
        {
            candidates.push(Candidate {
                collection: inner.collection().to_owned(),
                columns: covering,
                kind: CandidateKind::BTree,
                source: CandidateSource::Workload,
                needs_descriptor: false,
                benefit_per_byte: (template.weight() * prior / covering_width as f64).max(0.0),
            });
        }
    }
    if candidates.len() > MAX_CANDIDATES {
        return Err(CandidateError::BoundExceeded);
    }
    Ok(candidates)
}

fn candidate_column(field: &str, role: ColumnRole) -> Result<CandidateColumn, CandidateError> {
    CandidateColumn::try_new(field, role).map_err(|_| CandidateError::InvalidIdentifier)
}

/// Descriptive alias for integrations that use the operation's full name.
pub(crate) fn generate_candidates(
    template: &WeightedTemplate,
    sketches: &BTreeMap<(String, String), ColumnSketch>,
    extracted: &BTreeSet<String>,
) -> Result<Vec<Candidate>, CandidateError> {
    try_generate(template, sketches, extracted)
}

/// Convenience generation seam used by callers that treat an invalid or
/// over-sized proposal as no proposal.  The fallible [`try_generate`] seam is
/// preferred by lifecycle code that wants to record the refusal.
pub(crate) fn generate(
    template: &WeightedTemplate,
    sketches: &BTreeMap<(String, String), ColumnSketch>,
    extracted: &BTreeSet<String>,
) -> Vec<Candidate> {
    try_generate(template, sketches, extracted).unwrap_or_default()
}

/// Fallible spelling retained for callers that name the operation after the
/// legacy generator.
pub(crate) fn try_candidates_for_template(
    template: &WeightedTemplate,
    sketches: &BTreeMap<(String, String), ColumnSketch>,
    extracted: &BTreeSet<String>,
) -> Result<Vec<Candidate>, CandidateError> {
    try_generate(template, sketches, extracted)
}

/// Infallible, fail-closed spelling of [`try_candidates_for_template`].
pub(crate) fn candidates_for_template(
    template: &WeightedTemplate,
    sketches: &BTreeMap<(String, String), ColumnSketch>,
    extracted: &BTreeSet<String>,
) -> Vec<Candidate> {
    generate(template, sketches, extracted)
}

fn validate_template(template: &Template) -> Result<(), CandidateError> {
    if !valid_identifier(template.collection())
        || template.predicates().len() > MAX_CANDIDATE_FIELDS
        || template.ordering().len() > MAX_CANDIDATE_FIELDS
        || template.projection().is_some_and(|fields| fields.len() > MAX_CANDIDATE_FIELDS)
    {
        return Err(if valid_identifier(template.collection()) {
            CandidateError::BoundExceeded
        } else {
            CandidateError::InvalidIdentifier
        });
    }
    if template.predicates().iter().any(|predicate| !valid_identifier(predicate.field()))
        || template.ordering().iter().any(|field| !valid_identifier(field))
        || template
            .projection()
            .is_some_and(|fields| fields.iter().any(|field| !valid_identifier(field)))
    {
        return Err(CandidateError::InvalidIdentifier);
    }
    Ok(())
}

fn valid_identifier(identifier: &str) -> bool {
    crate::query::spec::validate_field_name(identifier).is_ok()
}

/// A compiler that asks SQLite about an index in a private scratch schema.
///
/// The connection is opened with `:memory:` and the candidate index is
/// dropped before every compile.  Only the `EXPLAIN QUERY PLAN` text is
/// retained; no candidate is registered with [`IndexCatalog`](crate::index::IndexCatalog)
/// and no production connection is reachable through this type.
pub(crate) struct SqliteHypotheticalCompiler {
    scratch: Connection,
    schema_version: u64,
}

/// Short adapter alias for planner integrations that do not need to mention
/// the implementation detail in their type name.
pub(crate) type SqliteCompiler = SqliteHypotheticalCompiler;

impl SqliteHypotheticalCompiler {
    /// Opens a scratch compiler for one positive schema version.
    pub(crate) fn new(schema_version: u64) -> Result<Self, CandidateError> {
        let schema_version_i64 =
            i64::try_from(schema_version).map_err(|_| CandidateError::InvalidSchemaVersion)?;
        if schema_version == 0 {
            return Err(CandidateError::InvalidSchemaVersion);
        }
        let scratch =
            Connection::open_in_memory().map_err(|_| CandidateError::ScratchSchemaFailure)?;
        scratch
            .pragma_update(None, "user_version", schema_version_i64)
            .map_err(|_| CandidateError::ScratchSchemaFailure)?;
        Ok(Self { scratch, schema_version })
    }

    /// Compiles a candidate after checking the caller's schema version.
    /// Unknown planner output and all scratch failures are refusals.
    pub(crate) fn compile_checked(
        &mut self,
        template: &Template,
        candidate: &Candidate,
        schema_version: u64,
    ) -> Result<CompileDecision, CandidateError> {
        if schema_version == 0 || schema_version != self.schema_version {
            return Err(CandidateError::InvalidSchemaVersion);
        }
        let actual_version: i64 = self
            .scratch
            .pragma_query_value(None, "user_version", |row| row.get(0))
            .map_err(|_| CandidateError::ScratchSchemaFailure)?;
        if u64::try_from(actual_version).ok() != Some(schema_version) {
            return Err(CandidateError::InvalidSchemaVersion);
        }
        validate_template(template)?;
        validate_candidate(candidate)?;
        if candidate.kind() != CandidateKind::BTree {
            return Err(CandidateError::UnsupportedCandidate);
        }

        self.scratch
            .execute_batch(
                "DROP INDEX IF EXISTS \"__terrabase_hypothetical_idx\";\n                 DROP TABLE IF EXISTS \"__terrabase_hypothetical\";",
            )
            .map_err(|_| CandidateError::ScratchSchemaFailure)?;
        let fields = template_fields(template, candidate)?;
        let mut create = String::from("CREATE TABLE \"__terrabase_hypothetical\" (");
        for (index, field) in fields.iter().enumerate() {
            if index > 0 {
                create.push_str(", ");
            }
            create.push('"');
            push_identifier(&mut create, field);
            create.push_str("\" TEXT");
        }
        create.push_str(");");
        self.scratch.execute_batch(&create).map_err(|_| CandidateError::ScratchSchemaFailure)?;

        // A small deterministic population gives SQLite enough cardinality to
        // distinguish a selective search from a full table scan without
        // carrying any production rows into the what-if schema.
        let placeholders = (1..=fields.len()).map(|index| format!("?{index}")).collect::<Vec<_>>();
        if !fields.is_empty() {
            let insert = format!(
                "INSERT INTO \"__terrabase_hypothetical\" ({}) VALUES ({})",
                fields.iter().map(|field| format!("\"{field}\"")).collect::<Vec<_>>().join(","),
                placeholders.join(",")
            );
            for row in 0..256_i64 {
                let values = fields
                    .iter()
                    .enumerate()
                    .map(|(index, _)| format!("{}-{}", index, row))
                    .collect::<Vec<_>>();
                self.scratch
                    .execute(&insert, rusqlite::params_from_iter(values.iter()))
                    .map_err(|_| CandidateError::ScratchSchemaFailure)?;
            }
        }

        let index_fields = candidate
            .columns()
            .iter()
            .map(|column| format!("\"{}\"", column.field()))
            .collect::<Vec<_>>()
            .join(",");
        let create_index = format!(
            "CREATE INDEX \"__terrabase_hypothetical_idx\" ON \"__terrabase_hypothetical\" ({index_fields})"
        );
        self.scratch
            .execute_batch(&create_index)
            .map_err(|_| CandidateError::ScratchSchemaFailure)?;

        let query = query_for(template, &fields)?;
        let explain = format!("EXPLAIN QUERY PLAN {query}");
        let mut statement =
            self.scratch.prepare(&explain).map_err(|_| CandidateError::ScratchSchemaFailure)?;
        let mut rows = statement
            .query(rusqlite::params_from_iter(
                template
                    .predicates()
                    .iter()
                    .filter(|predicate| {
                        !matches!(predicate.kind(), PredicateKind::FullText | PredicateKind::Other)
                    })
                    .map(|_| "0".to_owned()),
            ))
            .map_err(|_| CandidateError::ScratchSchemaFailure)?;
        let mut saw_row = false;
        let mut used = false;
        while let Some(row) = rows.next().map_err(|_| CandidateError::ScratchSchemaFailure)? {
            saw_row = true;
            let detail: String = row.get(3).map_err(|_| CandidateError::UnknownPlannerOutput)?;
            if detail.contains("USING COVERING INDEX __terrabase_hypothetical_idx")
                || detail.contains("USING INDEX __terrabase_hypothetical_idx")
            {
                used = true;
            } else if detail.starts_with("SCAN ") || detail.contains("USING INTEGER PRIMARY KEY") {
                // Recognized non-use: the candidate is rejected.
            } else {
                return Err(CandidateError::UnknownPlannerOutput);
            }
        }
        if !saw_row {
            return Err(CandidateError::UnknownPlannerOutput);
        }
        Ok(if used { CompileDecision::Used } else { CompileDecision::NotUsed })
    }
}

impl Default for SqliteHypotheticalCompiler {
    fn default() -> Self {
        Self::new(1).expect("schema version one is valid")
    }
}

impl HypotheticalCompiler for SqliteHypotheticalCompiler {
    fn compile(&mut self, template: &Template, candidate: &Candidate) -> CompileDecision {
        self.compile_checked(template, candidate, self.schema_version).unwrap_or_default()
    }
}

fn validate_candidate(candidate: &Candidate) -> Result<(), CandidateError> {
    if !valid_identifier(candidate.collection())
        || candidate.columns().iter().any(|column| !valid_identifier(column.field()))
    {
        return Err(CandidateError::InvalidIdentifier);
    }
    if candidate.columns().is_empty() || candidate.columns().len() > MAX_CANDIDATE_FIELDS {
        return Err(CandidateError::BoundExceeded);
    }
    let mut seen = BTreeSet::new();
    for column in candidate.columns() {
        if !seen.insert(column.field()) {
            return Err(CandidateError::InvalidIdentifier);
        }
    }
    Ok(())
}

fn template_fields(
    template: &Template,
    candidate: &Candidate,
) -> Result<Vec<String>, CandidateError> {
    let mut fields = BTreeSet::new();
    for predicate in template.predicates() {
        fields.insert(predicate.field().to_owned());
    }
    fields.extend(template.ordering().iter().cloned());
    if let Some(projection) = template.projection() {
        fields.extend(projection.iter().cloned());
    }
    fields.extend(candidate.columns().iter().map(|column| column.field().to_owned()));
    if fields.is_empty() || fields.len() > MAX_HYPOTHETICAL_FIELDS {
        return Err(CandidateError::BoundExceeded);
    }
    if fields.iter().any(|field| !valid_identifier(field)) {
        return Err(CandidateError::InvalidIdentifier);
    }
    Ok(fields.into_iter().collect())
}

fn push_identifier(sql: &mut String, identifier: &str) {
    for character in identifier.chars() {
        if character == '"' {
            sql.push_str("\"\"");
        } else {
            sql.push(character);
        }
    }
}

fn quote_identifier(identifier: &str) -> String {
    let mut quoted = String::with_capacity(identifier.len().saturating_add(2));
    quoted.push('"');
    push_identifier(&mut quoted, identifier);
    quoted.push('"');
    quoted
}

fn query_for(template: &Template, fields: &[String]) -> Result<String, CandidateError> {
    let projection = template
        .projection()
        .filter(|projection| !projection.is_empty())
        .map(|projection| {
            projection.iter().map(|field| quote_identifier(field)).collect::<Vec<_>>().join(",")
        })
        .unwrap_or_else(|| "*".to_owned());
    let mut query = format!("SELECT {projection} FROM \"__terrabase_hypothetical\"");
    let mut conditions = Vec::new();
    let mut parameter = 1_u32;
    for predicate in template.predicates() {
        let operator = match predicate.kind() {
            PredicateKind::Equality => "=",
            PredicateKind::Range => ">",
            PredicateKind::FullText => return Err(CandidateError::UnsupportedCandidate),
            PredicateKind::Other => continue,
        };
        conditions.push(format!("{} {operator} ?{parameter}", quote_identifier(predicate.field())));
        parameter += 1;
    }
    if !conditions.is_empty() {
        query.push_str(" WHERE ");
        query.push_str(&conditions.join(" AND "));
    }
    if !template.ordering().is_empty() {
        query.push_str(" ORDER BY ");
        query.push_str(
            &template
                .ordering()
                .iter()
                .map(|field| format!("{} ASC", quote_identifier(field)))
                .collect::<Vec<_>>()
                .join(","),
        );
    }
    // Keep this argument meaningful at the validation boundary: all fields
    // named by the generated statement must come from the scratch schema.
    let known_fields = fields.iter().map(String::as_str).collect::<BTreeSet<_>>();
    if template
        .predicates()
        .iter()
        .filter(|predicate| {
            !matches!(predicate.kind(), PredicateKind::FullText | PredicateKind::Other)
        })
        .any(|predicate| !known_fields.contains(predicate.field()))
        || template.ordering().iter().any(|field| !known_fields.contains(field.as_str()))
        || template.projection().is_some_and(|projection| {
            projection.iter().any(|field| !known_fields.contains(field.as_str()))
        })
    {
        return Err(CandidateError::InvalidIdentifier);
    }
    Ok(query)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::query::optimizer::auto_index::{ColumnRole, PredicateKind, TemplatePredicate};

    fn weighted(template: Template) -> WeightedTemplate {
        WeightedTemplate {
            template,
            executions: 1,
            total_cost: 100,
            average_cost: 100.0,
            weight: 100.0,
        }
    }

    #[test]
    fn generation_merges_prefix_in_the_frozen_order() {
        let template = Template::try_new(
            "orders",
            vec![
                TemplatePredicate::try_new("tenant", PredicateKind::Equality).expect("field"),
                TemplatePredicate::try_new("state", PredicateKind::Equality).expect("field"),
                TemplatePredicate::try_new("total", PredicateKind::Range).expect("field"),
            ],
            vec!["state".into(), "created".into()],
            Some(vec!["id".into(), "total".into()]),
        )
        .expect("template");
        let extracted = BTreeSet::from([
            String::from("tenant"),
            String::from("state"),
            String::from("total"),
            String::from("created"),
            String::from("id"),
        ]);
        let result =
            try_generate(&weighted(template), &BTreeMap::new(), &extracted).expect("candidate");
        assert_eq!(result.len(), 2, "base and covering candidates");
        let fields = result[0].fields();
        assert_eq!(fields, vec!["tenant", "state", "total", "created"]);
        assert_eq!(result[0].columns()[0].role(), ColumnRole::Equality);
        assert_eq!(result[0].columns()[2].role(), ColumnRole::Range);
        assert_eq!(result[0].columns()[3].role(), ColumnRole::Order);
        assert_eq!(result[1].fields(), vec!["tenant", "state", "total", "created", "id"]);
    }

    #[test]
    fn generation_fails_closed_at_the_field_bound() {
        let predicates = (0..=MAX_CANDIDATE_FIELDS)
            .map(|index| {
                TemplatePredicate::try_new(&format!("field{index}"), PredicateKind::Equality)
                    .expect("field")
            })
            .collect();
        let template = Template::try_new("orders", predicates, Vec::new(), None).expect("template");
        let extracted = (0..=MAX_CANDIDATE_FIELDS)
            .map(|index| format!("field{index}"))
            .collect::<BTreeSet<_>>();
        let error = try_generate(&weighted(template), &BTreeMap::new(), &extracted)
            .expect_err("bound refusal");
        assert_eq!(error, CandidateError::BoundExceeded);
    }

    #[test]
    fn compiler_rejects_zero_or_mismatched_schema_versions() {
        assert!(matches!(
            SqliteHypotheticalCompiler::new(0),
            Err(CandidateError::InvalidSchemaVersion)
        ));
        let template = Template::try_new(
            "orders",
            vec![TemplatePredicate::try_new("state", PredicateKind::Equality).expect("field")],
            Vec::new(),
            None,
        )
        .expect("template");
        let candidate = try_generate(
            &weighted(template.clone()),
            &BTreeMap::new(),
            &BTreeSet::from([String::from("state")]),
        )
        .expect("candidate")
        .pop()
        .expect("candidate");
        let mut compiler = SqliteHypotheticalCompiler::new(2).expect("compiler");
        assert_eq!(
            compiler.compile_checked(&template, &candidate, 1),
            Err(CandidateError::InvalidSchemaVersion)
        );
    }

    #[test]
    fn compiler_uses_only_its_scratch_schema() {
        let template = Template::try_new(
            "orders",
            vec![TemplatePredicate::try_new("state", PredicateKind::Equality).expect("field")],
            Vec::new(),
            None,
        )
        .expect("template");
        let candidate = try_generate(
            &weighted(template.clone()),
            &BTreeMap::new(),
            &BTreeSet::from([String::from("state")]),
        )
        .expect("candidate")
        .pop()
        .expect("candidate");
        let mut compiler = SqliteHypotheticalCompiler::default();
        let decision = compiler.compile(&template, &candidate);
        assert!(matches!(decision, CompileDecision::Used | CompileDecision::NotUsed));
    }
}
