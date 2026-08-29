use super::*;

use std::collections::{BTreeMap, BTreeSet};

fn scope() -> ResourceScope {
    ResourceScope::try_new("tenant-a", "project-a", "database-a").expect("scope")
}

fn context(generation: u64) -> PrincipalContext {
    let claims = PrincipalClaims::try_new([
        ("org", AttributeValue::String("acme".to_owned())),
        ("level", AttributeValue::Integer(3)),
    ])
    .expect("claims");
    PrincipalContext::from_verified(VerifiedPrincipalInput {
        scope: scope(),
        principal_id: "principal-a".to_owned(),
        actions: [Action::Read, Action::Query].into_iter().collect(),
        claims,
        audience: "terrascale.database".to_owned(),
        issued_at: 100,
        expires_at: 200,
        policy_generation: generation,
    })
    .expect("verified context")
}

fn fields() -> BTreeMap<String, AttributeValue> {
    [
        ("owner".to_owned(), AttributeValue::String("acme".to_owned())),
        ("name".to_owned(), AttributeValue::String("visible".to_owned())),
        ("secret".to_owned(), AttributeValue::String("hidden".to_owned())),
    ]
    .into_iter()
    .collect()
}

fn read_policy(generation: u64) -> Policy {
    let predicate =
        ClaimPredicate::try_new("owner", "org", ClaimComparison::Equals).expect("predicate");
    let projection = FieldProjection::try_allow(["owner", "name", "secret"])
        .expect("allow")
        .try_deny(["secret"])
        .expect("deny");
    let rule = PolicyRule::try_new(Operation::Read, "documents")
        .expect("rule")
        .with_claim_predicate(predicate)
        .with_projection(projection);
    Policy::try_new(generation, ["owner"], [rule]).expect("policy")
}

fn request(action: Action, now: u64) -> AuthorizationRequest {
    AuthorizationRequest::try_new(
        scope(),
        "terrascale.database",
        action,
        "documents",
        fields(),
        BTreeSet::new(),
        now,
    )
    .expect("request")
}

#[test]
fn policy_publication_fails_closed_for_unindexed_and_unbounded_rules() {
    let predicate =
        ClaimPredicate::try_new("owner", "org", ClaimComparison::Equals).expect("predicate");
    let rule = PolicyRule::try_new(Operation::Read, "documents")
        .expect("rule")
        .with_claim_predicate(predicate);
    assert_eq!(
        Policy::try_new(1, ["name"], [rule]),
        Err(AuthorizationError::UnindexedPredicate("owner".to_owned()))
    );

    assert_eq!(
        RelationshipPredicate::try_new("member", "users", 0, 1),
        Err(AuthorizationError::UnboundedRelationship)
    );
    assert_eq!(
        RelationshipPredicate::try_new("member", "users", 1, 0),
        Err(AuthorizationError::UnboundedRelationship)
    );
}

#[test]
fn policy_ast_is_canonical_and_relationship_evidence_is_bounded() {
    let relationship =
        RelationshipPredicate::try_new("member", "users", 1, 10).expect("relationship");
    let first = PolicyRule::try_new(Operation::Read, "documents")
        .expect("rule")
        .with_relationship(relationship.clone());
    let second = PolicyRule::try_new(Operation::Query, "documents").expect("rule");
    let left =
        Policy::try_new(7, ["owner", "name"], [first.clone(), second.clone()]).expect("policy");
    let right = Policy::try_new(7, ["name", "owner"], [second, first]).expect("policy");
    assert_eq!(left.canonical_bytes(), right.canonical_bytes());
    assert_eq!(relationship.max_hops(), 1);
    assert_eq!(relationship.max_matches(), 10);
}

#[test]
fn scope_audience_action_expiry_and_generation_are_all_fail_closed() {
    let policy = read_policy(1);
    assert_eq!(
        evaluate(&context(1), &policy, &request(Action::Read, 150)).fields().get("secret"),
        None
    );

    let mut wrong_scope = request(Action::Read, 150);
    wrong_scope.scope =
        ResourceScope::try_new("other-tenant", "project-a", "database-a").expect("scope");
    assert_eq!(
        evaluate(&context(1), &policy, &wrong_scope),
        AuthorizationDecision::Deny(DenialReason::ScopeMismatch)
    );

    let mut wrong_audience = request(Action::Read, 150);
    wrong_audience.audience = "other-audience".to_owned();
    assert_eq!(
        evaluate(&context(1), &policy, &wrong_audience),
        AuthorizationDecision::Deny(DenialReason::AudienceMismatch)
    );

    assert_eq!(
        evaluate(&context(1), &policy, &request(Action::Mutate, 150)),
        AuthorizationDecision::Deny(DenialReason::ActionNotGranted)
    );
    assert_eq!(
        evaluate(&context(1), &policy, &request(Action::Read, 200)),
        AuthorizationDecision::Deny(DenialReason::Expired)
    );
    assert_eq!(
        evaluate(&context(1), &policy, &request(Action::Read, 99)),
        AuthorizationDecision::Deny(DenialReason::Expired)
    );
    assert_eq!(
        evaluate(&context(2), &policy, &request(Action::Read, 150)),
        AuthorizationDecision::Deny(DenialReason::PolicyGenerationMismatch)
    );
}

#[test]
fn claim_predicates_and_field_projection_never_leak_denied_data() {
    let policy = read_policy(1);
    assert!(!format!("{:?}", context(1)).contains("acme"));
    let decision = evaluate(&context(1), &policy, &request(Action::Read, 150));
    let AuthorizationDecision::Allow { fields: redacted } = decision else {
        panic!("expected allow");
    };
    assert_eq!(redacted.get("owner"), Some(&AttributeValue::String("acme".to_owned())));
    assert_eq!(redacted.get("name"), Some(&AttributeValue::String("visible".to_owned())));
    assert!(!redacted.contains_key("secret"));

    let mut wrong_owner = fields();
    wrong_owner.insert("owner".to_owned(), AttributeValue::String("other".to_owned()));
    let denied_request = AuthorizationRequest::try_new(
        scope(),
        "terrascale.database",
        Action::Read,
        "documents",
        wrong_owner,
        BTreeSet::new(),
        150,
    )
    .expect("request");
    assert_eq!(
        evaluate(&context(1), &policy, &denied_request),
        AuthorizationDecision::Deny(DenialReason::PredicateFailed)
    );

    let ordering = ClaimPredicate::try_new("owner", "level", ClaimComparison::GreaterThan)
        .expect("ordering predicate");
    let ordering_policy = Policy::try_new(
        1,
        ["owner"],
        [PolicyRule::try_new(Operation::Read, "documents")
            .expect("rule")
            .with_claim_predicate(ordering)],
    )
    .expect("policy");
    assert_eq!(
        evaluate(&context(1), &ordering_policy, &request(Action::Read, 150)),
        AuthorizationDecision::Deny(DenialReason::PredicateFailed)
    );
}

#[test]
fn relationship_grants_require_matching_bounded_index_evidence() {
    let relationship =
        RelationshipPredicate::try_new("member", "users", 1, 10).expect("relationship");
    let rule = PolicyRule::try_new(Operation::Read, "documents")
        .expect("rule")
        .with_relationship(relationship.clone());
    let policy = Policy::try_new(1, std::iter::empty::<String>(), [rule]).expect("policy");
    assert_eq!(
        evaluate(&context(1), &policy, &request(Action::Read, 150)),
        AuthorizationDecision::Deny(DenialReason::PredicateFailed)
    );

    let mut evidence = BTreeSet::new();
    evidence.insert(relationship.key());
    let authorized = AuthorizationRequest::try_new(
        scope(),
        "terrascale.database",
        Action::Read,
        "documents",
        fields(),
        evidence,
        150,
    )
    .expect("request");
    assert!(evaluate(&context(1), &policy, &authorized).is_allowed());
}
