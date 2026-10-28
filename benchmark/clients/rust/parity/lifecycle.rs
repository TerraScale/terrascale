//! An uncertain create can still commit after a delete-by-name returns 404.
#[derive(Default)]
pub enum Creation {
    #[default]
    NotAttempted,
    Uncertain,
    Created(String),
}

impl Creation {
    pub fn id(&self) -> Option<&str> {
        match self {
            Self::Created(id) => Some(id),
            _ => None,
        }
    }

    pub fn state(&self) -> &'static str {
        match self {
            Self::NotAttempted => "not_attempted",
            Self::Uncertain => "uncertain",
            Self::Created(_) => "created",
        }
    }

    pub fn reconcile_required(&self, deletion: &str) -> bool {
        deletion != "logical_accepted" && (deletion != "absent" || matches!(self, Self::Uncertain))
    }
}

#[cfg(test)]
mod tests {
    use super::Creation;

    #[test]
    fn uncertain_create_and_absent_delete_need_reconciliation() {
        let created = Creation::Created("owned-id".into());
        assert_eq!(created.id(), Some("owned-id"));
        assert!(!created.reconcile_required("logical_accepted"));
        assert!(!created.reconcile_required("absent"));
        assert!(!Creation::NotAttempted.reconcile_required("absent"));
        assert!(Creation::Uncertain.reconcile_required("absent"));
        assert!(!Creation::Uncertain.reconcile_required("logical_accepted"));
        assert!(created.reconcile_required("failed"));
    }
}
