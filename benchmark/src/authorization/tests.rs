use super::*;

use crate::identity::{
    AuthorizationPolicyUlid, DatabaseUlid, PrincipalGrantUlid, PrincipalSessionUlid, PrincipalUlid,
    ProjectUlid, TenantUlid,
};

fn signed_payload() -> PrincipalContextPayload {
    PrincipalContextPayload::try_new(
        "production",
        "spiffe://terrascale/gateway",
        "terrascale-go",
        "terrascale.database",
        scope(),
        PrincipalUlid::from_text("01ARZ3NDEKTSV4RRFFQ69G5FB4").unwrap(),
        PrincipalSessionUlid::from_text("01ARZ3NDEKTSV4RRFFQ69G5FB5").unwrap(),
        PrincipalGrantUlid::from_text("01ARZ3NDEKTSV4RRFFQ69G5FB6").unwrap(),
        AuthorizationPolicyUlid::from_text("01ARZ3NDEKTSV4RRFFQ69G5FB7").unwrap(),
        BTreeSet::from([Action::Read, Action::Query]),
        PrincipalClaims::try_new([("role", AttributeValue::String("reader".to_owned()))]).unwrap(),
        7,
        1_000,
        1_300,
    )
    .unwrap()
}

fn signed_verifier(
    signer: &PrincipalContextSigner,
    mode: AuthorizationCryptoMode,
) -> PrincipalContextVerifier {
    let key = PrincipalContextKey::new(
        signer.key_id(),
        signer.public_key().unwrap(),
        signer.provenance(),
    )
    .unwrap();
    PrincipalContextVerifier::new(
        mode,
        "production",
        "spiffe://terrascale/gateway",
        "terrascale-go",
        "terrascale.database",
        PrincipalContextKeySet::try_new([key]).unwrap(),
    )
    .unwrap()
}

fn test_signer(key_id: &str) -> PrincipalContextSigner {
    let der = aws_lc_rs::signature::EcdsaKeyPair::generate_pkcs8(
        &aws_lc_rs::signature::ECDSA_P256_SHA256_FIXED_SIGNING,
        &aws_lc_rs::rand::SystemRandom::new(),
    )
    .unwrap();
    PrincipalContextSigner::from_pkcs8(
        key_id,
        der.as_ref().to_vec(),
        SigningKeyProvenance::LocalDevelopment,
    )
    .unwrap()
}
use std::collections::{BTreeMap, BTreeSet};

fn scope() -> ResourceScope {
    ResourceScope::try_new(
        TenantUlid::from_text("01ARZ3NDEKTSV4RRFFQ69G5FB1").unwrap(),
        ProjectUlid::from_text("01ARZ3NDEKTSV4RRFFQ69G5FB2").unwrap(),
        DatabaseUlid::from_text("01ARZ3NDEKTSV4RRFFQ69G5FB3").unwrap(),
    )
    .expect("scope")
}

fn context(generation: u64) -> PrincipalContext {
    let claims = PrincipalClaims::try_new([
        ("org", AttributeValue::String("acme".to_owned())),
        ("level", AttributeValue::Integer(3)),
    ])
    .expect("claims");
    PrincipalContext::from_verified(VerifiedPrincipalInput {
        scope: scope(),
        environment: "production".to_owned(),
        gateway_spiffe_principal: "spiffe://terrascale/gateway".to_owned(),
        issuer: "terrascale-go".to_owned(),
        principal_id: PrincipalUlid::from_text("01ARZ3NDEKTSV4RRFFQ69G5FB4").unwrap(),
        session_id: PrincipalSessionUlid::from_text("01ARZ3NDEKTSV4RRFFQ69G5FB5").unwrap(),
        grant_id: PrincipalGrantUlid::from_text("01ARZ3NDEKTSV4RRFFQ69G5FB6").unwrap(),
        policy_id: AuthorizationPolicyUlid::from_text("01ARZ3NDEKTSV4RRFFQ69G5FB7").unwrap(),
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
    wrong_scope.scope = ResourceScope::try_new(
        TenantUlid::from_text("01ARZ3NDEKTSV4RRFFQ69G5FC1").unwrap(),
        ProjectUlid::from_text("01ARZ3NDEKTSV4RRFFQ69G5FB2").unwrap(),
        DatabaseUlid::from_text("01ARZ3NDEKTSV4RRFFQ69G5FB3").unwrap(),
    )
    .expect("scope");
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
fn signed_context_is_canonical_authenticated_and_bound() {
    let signer = test_signer("local-k1");
    let payload = signed_payload();
    assert_eq!(payload.canonical_bytes(), payload.canonical_bytes());
    let envelope = signer.sign(&payload, AuthorizationCryptoMode::LocalDevelopment).unwrap();
    let encoded = envelope.as_bytes();
    assert_eq!(PrincipalContextEnvelope::from_bytes(&encoded).unwrap(), envelope);
    let verifier = signed_verifier(&signer, AuthorizationCryptoMode::LocalDevelopment);
    let context = verifier.verify(&envelope, 1_100).unwrap();
    assert_eq!(context.principal_id(), payload.principal_id());
    assert_eq!(context.session_id(), payload.session_id());
    assert_eq!(context.grant_id(), payload.grant_id());
    assert_eq!(context.policy_id(), payload.policy_id());

    let mut tampered = encoded;
    tampered[12 + envelope.key_id().len() + 4] ^= 1;
    let tampered = PrincipalContextEnvelope::from_bytes(&tampered).unwrap();
    assert_eq!(verifier.verify(&tampered, 1_100), Err(AuthorizationError::InvalidContextSignature));
}

#[test]
fn signed_context_rejects_wrong_binding_time_key_and_replay() {
    let signer = test_signer("local-k2");
    let payload = signed_payload();
    let envelope = signer.sign(&payload, AuthorizationCryptoMode::LocalDevelopment).unwrap();
    let verifier = signed_verifier(&signer, AuthorizationCryptoMode::LocalDevelopment);
    assert_eq!(verifier.verify(&envelope, 999), Err(AuthorizationError::ContextNotYetValid));
    assert_eq!(verifier.verify(&envelope, 1_300), Err(AuthorizationError::ContextExpired));

    let wrong = PrincipalContextVerifier::new(
        AuthorizationCryptoMode::LocalDevelopment,
        "other-environment",
        "spiffe://terrascale/gateway",
        "terrascale-go",
        "terrascale.database",
        PrincipalContextKeySet::try_new([PrincipalContextKey::new(
            signer.key_id(),
            signer.public_key().unwrap(),
            signer.provenance(),
        )
        .unwrap()])
        .unwrap(),
    )
    .unwrap();
    assert_eq!(wrong.verify(&envelope, 1_100), Err(AuthorizationError::ContextBindingMismatch));

    let other = test_signer("other");
    let wrong_key = signed_verifier(&other, AuthorizationCryptoMode::LocalDevelopment);
    assert_eq!(wrong_key.verify(&envelope, 1_100), Err(AuthorizationError::UnknownContextKey));

    let mut guard = PrincipalContextReplayGuard::new();
    assert!(verifier.verify_with_replay(&envelope, 1_100, b"channel-a", &mut guard).is_ok());
    assert_eq!(
        verifier.verify_with_replay(&envelope, 1_100, b"channel-a", &mut guard),
        Err(AuthorizationError::ContextReplay)
    );
    assert!(verifier.verify_with_replay(&envelope, 1_100, b"channel-b", &mut guard).is_ok());
}

#[test]
fn signed_context_rejects_development_keys_in_production() {
    let signer = test_signer("local-prod");
    let key = PrincipalContextKey::new(
        signer.key_id(),
        signer.public_key().unwrap(),
        SigningKeyProvenance::LocalDevelopment,
    )
    .unwrap();
    assert!(matches!(
        PrincipalContextVerifier::new(
            AuthorizationCryptoMode::ProductionFips,
            "production",
            "spiffe://terrascale/gateway",
            "terrascale-go",
            "terrascale.database",
            PrincipalContextKeySet::try_new([key]).unwrap(),
        ),
        Err(AuthorizationError::DevelopmentSigningKey)
    ));
    assert_eq!(
        signer.sign(&signed_payload(), AuthorizationCryptoMode::ProductionFips),
        Err(AuthorizationError::DevelopmentSigningKey)
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
fn one_failed_conditional_grant_does_not_mask_another_grant() {
    // Canonical ordering puts the `aaa` rule first. It cannot match because
    // the request intentionally lacks that field; the `zzz` grant must still
    // be considered and allow the operation.
    let first =
        PolicyRule::try_new(Operation::Read, "documents").expect("rule").with_claim_predicate(
            ClaimPredicate::try_new("aaa", "org", ClaimComparison::Equals).expect("predicate"),
        );
    let second =
        PolicyRule::try_new(Operation::Read, "documents").expect("rule").with_claim_predicate(
            ClaimPredicate::try_new("zzz", "org", ClaimComparison::Equals).expect("predicate"),
        );
    let policy = Policy::try_new(1, ["aaa", "zzz"], [first, second]).expect("policy");
    let mut authorized = request(Action::Read, 150);
    authorized.fields.insert("zzz".to_owned(), AttributeValue::String("acme".to_owned()));
    assert!(evaluate(&context(1), &policy, &authorized).is_allowed());
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
