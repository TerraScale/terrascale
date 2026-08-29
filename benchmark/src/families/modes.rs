//! CRDT-aware schema modes (P3, the frozen schema-merge contract §1-§3).
//!
//! Every collection selects exactly one [`SchemaMode`], recorded in
//! collection metadata, default `schema-free`:
//!
//! | Mode | Declared paths | Undeclared paths |
//! | --- | --- | --- |
//! | `schema-free` | none required | accepted; stable CRDT kind recorded at first write |
//! | `validated` | versioned rules enforced | accepted |
//! | `strict` | versioned rules enforced | rejected with a typed error |
//!
//! Two validation points are required (contract §2): admission —
//! before durable acceptance, nothing durable on refusal — and
//! post-merge — after any CRDT merge the materialized document is
//! re-validated, and an invalid merged state is quarantined from
//! reads with an auditable receipt, never silently visible.
//!
//! The SM-02 rule set (type/shape, enum, range, length) is
//! merge-safe — closed under merge — so it runs coordination-free at
//! admission and single-document CRDT operations stay available during
//! a partition. Cross-field, uniqueness, and compare-exchange rules
//! are NOT closed under merge; they belong to the coordinated
//! transaction path and are classified separately (contract §3).

use std::collections::BTreeMap;

use super::{SchemaDefinition, SchemaRule, SchemaViolation};
use crate::types::DocumentValue;

/// One collection's selected schema mode (contract §1).
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub enum SchemaMode {
    /// No declared paths required. A stable CRDT kind is recorded per
    /// path once created; a later write of an incompatible kind at the
    /// same path is rejected — silently re-kinding a path breaks
    /// convergence.
    #[default]
    SchemaFree,
    /// Versioned rules enforced on declared paths; undeclared paths
    /// accepted.
    Validated,
    /// Versioned rules enforced on declared paths; undeclared paths
    /// rejected with a typed error.
    Strict,
}

/// The CRDT value kind of one document path — the stability unit of
/// `schema-free` collections.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ValueKind {
    /// A null value.
    Null,
    /// A boolean value.
    Boolean,
    /// A signed integer value.
    Integer,
    /// A floating-point value.
    Number,
    /// A text value.
    String,
    /// An ordered sequence.
    Array,
    /// A field map.
    Object,
}

impl ValueKind {
    /// Returns the kind of one value.
    #[must_use]
    pub fn of(value: &DocumentValue) -> Self {
        match value {
            DocumentValue::Null => Self::Null,
            DocumentValue::Boolean(_) => Self::Boolean,
            DocumentValue::Integer(_) => Self::Integer,
            DocumentValue::Number(_) => Self::Number,
            DocumentValue::String(_) => Self::String,
            DocumentValue::Array(_) => Self::Array,
            DocumentValue::Object(_) => Self::Object,
        }
    }
}

/// One collection's schema policy: its mode and, for `validated` and
/// `strict`, the governing versioned definition.
#[derive(Clone, Debug, PartialEq)]
pub struct CollectionPolicy {
    mode: SchemaMode,
    definition: Option<SchemaDefinition>,
}

impl Default for CollectionPolicy {
    /// Returns the `schema-free` policy with no definition.
    fn default() -> Self {
        Self { mode: SchemaMode::SchemaFree, definition: None }
    }
}

impl CollectionPolicy {
    /// Builds the `schema-free` policy.
    #[must_use]
    pub fn schema_free() -> Self {
        Self::default()
    }

    /// Builds the `validated` policy over one definition.
    #[must_use]
    pub fn validated(definition: SchemaDefinition) -> Self {
        Self { mode: SchemaMode::Validated, definition: Some(definition) }
    }

    /// Builds the `strict` policy over one definition.
    #[must_use]
    pub fn strict(definition: SchemaDefinition) -> Self {
        Self { mode: SchemaMode::Strict, definition: Some(definition) }
    }

    /// Returns the collection's mode.
    #[must_use]
    pub const fn mode(&self) -> SchemaMode {
        self.mode
    }

    /// Returns the governing definition, when the mode has one.
    #[must_use]
    pub fn definition(&self) -> Option<&SchemaDefinition> {
        self.definition.as_ref()
    }

    /// Returns the declared path set, empty for `schema-free`.
    #[must_use]
    pub fn declared_paths(&self) -> Vec<String> {
        let Some(definition) = &self.definition else {
            return Vec::new();
        };
        let mut paths: Vec<String> =
            definition.rules().iter().map(SchemaRule::field).map(str::to_owned).collect();
        paths.sort();
        paths.dedup();
        paths
    }
}

/// Why a mutation was not admitted (contract §2, validation point 1).
/// Nothing durable happens on refusal.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum AdmissionError {
    /// A `strict` collection rejected an undeclared path.
    UndeclaredPath {
        /// The rejected path.
        path: String,
    },
    /// A `schema-free` path's stable CRDT kind was violated: silently
    /// re-kinding a path breaks convergence, so the write fails
    /// closed.
    KindMismatch {
        /// The re-kinded path.
        path: String,
        /// The kind recorded at the path's first write.
        expected: ValueKind,
        /// The kind this write offered.
        offered: ValueKind,
    },
    /// The governing definition's rules rejected the body.
    RuleViolations {
        /// The rule violations, in the definition's canonical order.
        violations: Vec<SchemaViolation>,
    },
}

/// The per-path CRDT kinds a collection has recorded — the
/// `schema-free` stability registry. The kinds are derivable from the
/// materialized state itself; this type is the lens over them.
pub type KnownKinds = BTreeMap<String, ValueKind>;

/// Admits one whole-body mutation against a collection's policy
/// (contract §2, validation point 1: before durable acceptance).
///
/// `known_kinds` is the path-kind registry the accepting state already
/// holds; a `schema-free` write against a path whose recorded kind
/// differs from the offered kind fails closed.
///
/// # Errors
///
/// Returns the typed [`AdmissionError`]; the caller records nothing
/// durable on refusal.
pub fn admit(
    policy: &CollectionPolicy,
    body: &BTreeMap<String, DocumentValue>,
    known_kinds: &KnownKinds,
) -> Result<(), AdmissionError> {
    match policy.mode() {
        SchemaMode::SchemaFree => {
            for (path, value) in body {
                let offered = ValueKind::of(value);
                if let Some(&expected) = known_kinds.get(path)
                    && expected != offered
                {
                    return Err(AdmissionError::KindMismatch {
                        path: path.clone(),
                        expected,
                        offered,
                    });
                }
            }
            Ok(())
        }
        SchemaMode::Validated | SchemaMode::Strict => {
            let Some(definition) = policy.definition() else {
                // Unreachable through the constructors: validated and
                // strict policies always carry a definition.
                return Ok(());
            };
            if policy.mode() == SchemaMode::Strict {
                let declared = policy.declared_paths();
                for path in body.keys() {
                    if !declared.contains(path) {
                        return Err(AdmissionError::UndeclaredPath { path: path.clone() });
                    }
                }
            }
            let value = DocumentValue::Object(body.clone());
            let violations = super::schemas::violations(definition, &value);
            if violations.is_empty() {
                Ok(())
            } else {
                Err(AdmissionError::RuleViolations { violations })
            }
        }
    }
}

/// Validates one merged document body against a collection's policy
/// (contract §2, validation point 2: after any CRDT merge).
///
/// `schema-free` never quarantines: its single stability rule is a
/// per-write admission fact, and a merged document holds exactly one
/// value per path. `validated` and `strict` re-run their rule set —
/// the merged state can violate under policy skew (one Member
/// validated under an older version) or a merge that drops a required
/// path.
#[must_use]
pub fn merged_violations(
    policy: &CollectionPolicy,
    body: &BTreeMap<String, DocumentValue>,
) -> Vec<SchemaViolation> {
    let Some(definition) = policy.definition() else {
        return Vec::new();
    };
    let value = DocumentValue::Object(body.clone());
    super::schemas::violations(definition, &value)
}

/// Reports whether a violation list carries any non-empty field — the
/// document-level shape failure (`DocumentNotObject`) reports against
/// the document itself.
#[must_use]
pub fn is_document_level(violations: &[SchemaViolation]) -> bool {
    violations.iter().any(|violation| violation.field().is_empty())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::families::{SchemaRule, SchemaVersion, ViolationKind};

    fn definition(rules: Vec<SchemaRule>) -> SchemaDefinition {
        SchemaDefinition::try_new(SchemaVersion::try_new(1).expect("valid version"), rules)
            .expect("valid definition")
    }

    fn body(entries: &[(&str, DocumentValue)]) -> BTreeMap<String, DocumentValue> {
        entries.iter().map(|(path, value)| ((*path).to_owned(), value.clone())).collect()
    }

    #[test]
    fn schema_free_records_and_enforces_stable_path_kinds() {
        let policy = CollectionPolicy::schema_free();
        let mut known: KnownKinds = BTreeMap::new();
        let text = body(&[("status", DocumentValue::String("open".to_owned()))]);
        assert_eq!(admit(&policy, &text, &known), Ok(()));
        known.insert("status".to_owned(), ValueKind::of(&DocumentValue::String("x".to_owned())));

        // Same kind: admitted.
        assert_eq!(admit(&policy, &text, &known), Ok(()));
        // Re-kinding the path fails closed.
        let number = body(&[("status", DocumentValue::Integer(7))]);
        assert_eq!(
            admit(&policy, &number, &known),
            Err(AdmissionError::KindMismatch {
                path: "status".to_owned(),
                expected: ValueKind::String,
                offered: ValueKind::Integer,
            })
        );
    }

    #[test]
    fn validated_enforces_rules_and_accepts_undeclared_paths() {
        let policy = CollectionPolicy::validated(definition(vec![
            SchemaRule::Required { field: "status".to_owned() },
            SchemaRule::EnumValues {
                field: "status".to_owned(),
                values: vec!["open".to_owned(), "closed".to_owned()],
            },
        ]));
        // An undeclared path is accepted alongside valid declared ones.
        let admitted = body(&[
            ("status", DocumentValue::String("open".to_owned())),
            ("extra", DocumentValue::Integer(1)),
        ]);
        assert_eq!(admit(&policy, &admitted, &KnownKinds::new()), Ok(()));
        // A rule violation is refused with the violation named.
        let refused = body(&[("status", DocumentValue::String("paused".to_owned()))]);
        let Err(AdmissionError::RuleViolations { violations }) =
            admit(&policy, &refused, &KnownKinds::new())
        else {
            panic!("the enum rule refuses the unknown value");
        };
        assert_eq!(violations.len(), 1);
        assert_eq!(violations[0].field(), "status");
        // A missing required field is refused.
        let missing = body(&[("extra", DocumentValue::Integer(1))]);
        assert!(matches!(
            admit(&policy, &missing, &KnownKinds::new()),
            Err(AdmissionError::RuleViolations { .. })
        ));
    }

    #[test]
    fn strict_rejects_undeclared_paths() {
        let policy = CollectionPolicy::strict(definition(vec![SchemaRule::Required {
            field: "status".to_owned(),
        }]));
        let admitted = body(&[("status", DocumentValue::String("open".to_owned()))]);
        assert_eq!(admit(&policy, &admitted, &KnownKinds::new()), Ok(()));
        let refused = body(&[
            ("status", DocumentValue::String("open".to_owned())),
            ("undeclared", DocumentValue::Integer(1)),
        ]);
        assert_eq!(
            admit(&policy, &refused, &KnownKinds::new()),
            Err(AdmissionError::UndeclaredPath { path: "undeclared".to_owned() })
        );
    }

    #[test]
    fn merged_bodies_are_revalidated_under_declared_rules() {
        let policy = CollectionPolicy::validated(definition(vec![SchemaRule::Required {
            field: "status".to_owned(),
        }]));
        // A merged body dropping the required path is a quarantine
        // reason; a conforming merged body is not.
        let quarantined = body(&[("note", DocumentValue::String("n".to_owned()))]);
        let violations = merged_violations(&policy, &quarantined);
        assert_eq!(violations.len(), 1);
        assert_eq!(violations[0].kind(), ViolationKind::MissingRequiredField);
        let conforming = body(&[("status", DocumentValue::String("open".to_owned()))]);
        assert!(merged_violations(&policy, &conforming).is_empty());
        // schema-free never quarantines a merged body.
        assert!(merged_violations(&CollectionPolicy::schema_free(), &quarantined).is_empty());
    }

    #[test]
    fn declared_paths_are_canonical_and_deduplicated() {
        let policy = CollectionPolicy::strict(definition(vec![
            SchemaRule::Required { field: "b".to_owned() },
            SchemaRule::TextLength { field: "a".to_owned(), min: None, max: Some(4) },
            SchemaRule::EnumValues { field: "b".to_owned(), values: vec!["x".to_owned()] },
        ]));
        assert_eq!(policy.declared_paths(), vec!["a".to_owned(), "b".to_owned()]);
        assert!(CollectionPolicy::schema_free().declared_paths().is_empty());
    }
}
