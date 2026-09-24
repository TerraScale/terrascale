use auth_app::{
    AuthOptions,
    app::{AppState, build_state_with_mode},
    build_router,
    features::users,
};
use auth_contracts::models::{
    ApiKey, AuditEvent, Instance, Invitation, OrgDomain, OrgMembership, Organization,
    OrganizationStatus, Session, User, VerificationToken,
};
use auth_security::tokens::sha256_hex;
use axum::http::header;
use reqwest::{Client, StatusCode};
use serde_json::{Value, json};
use std::time::{SystemTime, UNIX_EPOCH};
use tokio::{net::TcpListener, task::JoinHandle};

mod support;

use support::terrabase_http::{TEST_TERRASCALE_API_KEY, TerraBaseHttpFixture};

struct TestServer {
    origin: String,
    state: AppState,
    task: JoinHandle<()>,
    storage: TerraBaseHttpFixture,
}

impl TestServer {
    async fn start() -> Self {
        let storage = TerraBaseHttpFixture::start().await;
        let listener = TcpListener::bind("127.0.0.1:0")
            .await
            .expect("app listener should bind");
        let address = listener
            .local_addr()
            .expect("app listener should have an address");
        let public_origin = format!("http://localhost:{}", address.port());
        static NEXT_DATABASE: std::sync::atomic::AtomicU64 = std::sync::atomic::AtomicU64::new(1);
        let database = format!(
            "ts-auth-org-regression-{}",
            NEXT_DATABASE.fetch_add(1, std::sync::atomic::Ordering::Relaxed)
        );
        let options = AuthOptions {
            listen: "http://127.0.0.1:0".into(),
            public_origin: public_origin.clone(),
            terrascale_api_url: storage.origin.clone(),
            terrascale_api_key: TEST_TERRASCALE_API_KEY.into(),
            terrascale_project_id: None,
            terrascale_tenant_id: None,
            database,
            master_key: vec![7; 32],
            audit_signing_key: Some(vec![8; 32]),
            audit_verification_keys: Vec::new(),
            operator_key: "test-only-operator-key".into(),
            metrics_key: Some("test-only-metrics-key".into()),
            rate_limit_per_window: 1_000,
            stripe_secret_key: None,
            stripe_webhook_secret: None,
            stripe_api_url: "http://127.0.0.1:1".into(),
            price_pro: None,
            price_enterprise: None,
            smtp_host: None,
            smtp_port: 587,
            smtp_username: None,
            smtp_password: None,
            smtp_from_address: None,
        };
        let state = build_state_with_mode(options, true).expect("test app state should build");
        let router = build_router(state.clone());
        let task = tokio::spawn(async move {
            axum::serve(listener, router)
                .await
                .expect("test app should remain available");
        });
        Self {
            origin: format!("http://{address}"),
            state,
            task,
            storage,
        }
    }

    fn client(&self) -> Client {
        Client::builder()
            .redirect(reqwest::redirect::Policy::none())
            .build()
            .expect("client should build")
    }
}

impl Drop for TestServer {
    fn drop(&mut self) {
        self.task.abort();
    }
}

fn now() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .expect("system time should be valid")
        .as_secs() as i64
}

async fn create_instance(server: &TestServer, slug: &str) -> Instance {
    let instance = Instance {
        id: format!("ins_{slug}"),
        slug: slug.into(),
        name: format!("{slug} test"),
        logo_url: None,
        theme_color: None,
        stripe_customer_id: None,
        created_at: now(),
    };
    server
        .state
        .store
        .create_instance(&instance)
        .await
        .expect("instance should persist");
    instance
}

async fn create_deletable_organization(
    server: &TestServer,
    slug: &str,
) -> (Instance, Organization, String) {
    let instance = create_instance(server, slug).await;
    let current_time = now();
    let secret = format!("sk_organization_delete_{slug}");
    let api_key = ApiKey {
        id: format!("key_organization_delete_{slug}"),
        instance_id: instance.id.clone(),
        name: "organization deletion test".into(),
        secret_key: sha256_hex(&secret),
        scopes: vec!["organizations".into()],
        created_at: current_time,
        last_used_at: None,
        revoked: false,
    };
    server
        .state
        .store
        .create("api_keys", &api_key.id, &api_key)
        .await
        .expect("organization-scoped API key should persist");
    let organization = Organization {
        id: format!("org_{slug}"),
        instance_id: instance.id.clone(),
        name: "Deletion test team".into(),
        slug_key: format!("{}:deletion-test-team", instance.id),
        status: OrganizationStatus::Active,
        public_metadata: Default::default(),
        created_at: current_time,
    };
    server
        .state
        .store
        .create_organization(&organization)
        .await
        .expect("organization should persist");
    (instance, organization, secret)
}

async fn seed_record(server: &TestServer, collection: &str, id: &str, value: &Value) {
    server
        .state
        .store
        .create(collection, id, value)
        .await
        .unwrap_or_else(|error| panic!("seed {collection}/{id}: {error}"));
}

#[tokio::test]
async fn failed_invitation_membership_create_can_be_retried_with_the_same_link() {
    let server = TestServer::start().await;
    let instance = create_instance(&server, "invitation-retry").await;
    let client = server.client();
    let current_time = now();
    let organization = Organization {
        id: "org_invitation_retry".into(),
        instance_id: instance.id.clone(),
        name: "Retry Team".into(),
        slug_key: format!("{}:retry-team", instance.id),
        status: OrganizationStatus::Active,
        public_metadata: Default::default(),
        created_at: current_time,
    };
    server
        .state
        .store
        .create_organization(&organization)
        .await
        .expect("organization should persist");
    seed_record(
        &server,
        "users",
        "usr_invitation_retry",
        &json!({
            "id": "usr_invitation_retry",
            "instanceId": instance.id,
            "email": "invitee@example.test",
            "emailKey": format!("{}:invitee@example.test", instance.id),
            "emailVerified": true,
            "createdAt": current_time,
            "updatedAt": current_time,
        }),
    )
    .await;
    let session_secret = "session-for-invitation-retry";
    let session = Session {
        id: "ses_invitation_retry".into(),
        instance_id: instance.id.clone(),
        user_id: "usr_invitation_retry".into(),
        token_key: sha256_hex(session_secret),
        status: "active".into(),
        auth_epoch: 0,
        created_at: current_time,
        expires_at: current_time + 3_600,
        last_active_at: Some(current_time),
        user_agent: None,
        ip_address: None,
    };
    server
        .state
        .store
        .create("sessions", &session.id, &session)
        .await
        .expect("session should persist");
    let invitation = Invitation {
        id: "inv_invitation_retry".into(),
        instance_id: instance.id.clone(),
        org_id: organization.id.clone(),
        email: "invitee@example.test".into(),
        role: "member".into(),
        status: "pending".into(),
        expires_at: current_time + 3_600,
        created_at: current_time,
    };
    server
        .state
        .store
        .create_invitation(&invitation)
        .await
        .expect("invitation should persist");
    let acceptance_token = users::issue_token(
        &server.state,
        &instance.id,
        Some(&invitation.id),
        "invitation",
        3_600,
    )
    .await
    .expect("acceptance token should persist");

    server
        .storage
        .fail_next_create_only_before_commit("memberships");
    let acceptance_url = format!(
        "{}/ui/{}/invitations/{}/accept",
        server.origin, instance.slug, invitation.id
    );
    let failed = client
        .get(&acceptance_url)
        .query(&[("token", acceptance_token.as_str())])
        .header(header::COOKIE, format!("tsauth_session={session_secret}"))
        .send()
        .await
        .expect("first acceptance request should reach the app");
    assert_eq!(failed.status(), StatusCode::INTERNAL_SERVER_ERROR);

    let after_failure: Invitation = server
        .state
        .store
        .get("invitations", &invitation.id)
        .await
        .expect("invitation lookup should succeed")
        .expect("invitation remains after membership failure");
    assert_eq!(after_failure.status, "accepting");
    assert!(
        server
            .state
            .store
            .list_memberships_by_org(&organization.id)
            .await
            .expect("membership lookup should succeed")
            .is_empty()
    );
    let token_after_failure: VerificationToken = server
        .state
        .store
        .query_one(
            "verification_tokens",
            "tokenKey",
            &sha256_hex(&acceptance_token),
        )
        .await
        .expect("token lookup should succeed")
        .expect("acceptance token remains after membership failure");
    assert!(!token_after_failure.consumed);

    seed_record(
        &server,
        "users",
        "usr_wrong_invitation_account",
        &json!({
            "id": "usr_wrong_invitation_account",
            "instanceId": instance.id,
            "email": "other@example.test",
            "emailKey": format!("{}:other@example.test", instance.id),
            "emailVerified": true,
            "createdAt": current_time,
            "updatedAt": current_time,
        }),
    )
    .await;
    let other_session_secret = "session-for-wrong-invitation-account";
    let other_session = Session {
        id: "ses_wrong_invitation_account".into(),
        instance_id: instance.id.clone(),
        user_id: "usr_wrong_invitation_account".into(),
        token_key: sha256_hex(other_session_secret),
        status: "active".into(),
        auth_epoch: 0,
        created_at: current_time,
        expires_at: current_time + 3_600,
        last_active_at: Some(current_time),
        user_agent: None,
        ip_address: None,
    };
    server
        .state
        .store
        .create("sessions", &other_session.id, &other_session)
        .await
        .expect("other session should persist");
    let wrong_account = client
        .get(&acceptance_url)
        .query(&[("token", acceptance_token.as_str())])
        .header(
            header::COOKIE,
            format!("tsauth_session={other_session_secret}"),
        )
        .send()
        .await
        .expect("wrong-account retry should reach the app");
    assert_eq!(wrong_account.status(), StatusCode::OK);
    assert!(
        wrong_account
            .text()
            .await
            .expect("wrong-account page should be readable")
            .contains("Wrong account")
    );
    assert!(
        server
            .state
            .store
            .list_memberships_by_org(&organization.id)
            .await
            .expect("membership lookup should succeed")
            .is_empty()
    );

    let retried = client
        .get(&acceptance_url)
        .query(&[("token", acceptance_token.as_str())])
        .header(header::COOKIE, format!("tsauth_session={session_secret}"))
        .send()
        .await
        .expect("retry should reach the app");
    assert_eq!(retried.status(), StatusCode::OK);
    let html = retried.text().await.expect("success page should be HTML");
    assert!(html.contains("Invitation accepted"));
    assert_eq!(
        server
            .state
            .store
            .get::<Invitation>("invitations", &invitation.id)
            .await
            .expect("invitation lookup should succeed")
            .expect("invitation should remain")
            .status,
        "accepted"
    );
    assert_eq!(
        server
            .state
            .store
            .list_memberships_by_org(&organization.id)
            .await
            .expect("membership lookup should succeed")
            .len(),
        1
    );
    assert!(
        server
            .state
            .store
            .query_one::<VerificationToken>(
                "verification_tokens",
                "tokenKey",
                &sha256_hex(&acceptance_token),
            )
            .await
            .expect("token lookup should succeed")
            .expect("acceptance token remains available for state inspection")
            .consumed
    );
}

#[tokio::test]
async fn direct_domain_enrollment_requires_verified_email_and_current_organization() {
    let server = TestServer::start().await;
    let instance = create_instance(&server, "manual-domain-enrollment").await;
    let client = server.client();
    let current_time = now();
    let secret = "sk_domain_enrollment_test";
    let api_key = ApiKey {
        id: "key_domain_enrollment".into(),
        instance_id: instance.id.clone(),
        name: "domain enrollment test".into(),
        secret_key: sha256_hex(secret),
        scopes: vec!["domains".into()],
        created_at: current_time,
        last_used_at: None,
        revoked: false,
    };
    server
        .state
        .store
        .create("api_keys", &api_key.id, &api_key)
        .await
        .expect("domain-scoped API key should persist");
    seed_record(
        &server,
        "users",
        "usr_domain_enrollment",
        &json!({
            "id": "usr_domain_enrollment",
            "instanceId": instance.id,
            "email": "member@example.test",
            "emailKey": format!("{}:member@example.test", instance.id),
            "emailVerified": false,
            "createdAt": current_time,
            "updatedAt": current_time,
        }),
    )
    .await;
    server
        .state
        .store
        .create(
            "org_domains",
            "domain_manual_enrollment",
            &OrgDomain {
                id: "domain_manual_enrollment".into(),
                instance_id: instance.id.clone(),
                org_id: "org_created_later".into(),
                domain: "example.test".into(),
                domain_key: format!("{}:example.test", instance.id),
                created_at: current_time,
            },
        )
        .await
        .expect("domain policy should persist");
    let url = format!(
        "{}/v1/{}/domains/example.test/enroll",
        server.origin, instance.slug
    );
    let unverified = client
        .post(&url)
        .bearer_auth(secret)
        .json(&json!({ "user_id": "usr_domain_enrollment" }))
        .send()
        .await
        .expect("unverified enrollment request should reach the app");
    assert_eq!(unverified.status(), StatusCode::FORBIDDEN);
    assert!(
        server
            .state
            .store
            .list_memberships_by_org("org_created_later")
            .await
            .expect("membership lookup should succeed")
            .is_empty()
    );

    let mut user: User = server
        .state
        .store
        .get("users", "usr_domain_enrollment")
        .await
        .expect("user lookup should succeed")
        .expect("user should remain");
    user.email_verified = true;
    server
        .state
        .store
        .upsert("users", &user.id, &user)
        .await
        .expect("verified email state should persist");

    let missing_owner = client
        .post(&url)
        .bearer_auth(secret)
        .json(&json!({ "user_id": "usr_domain_enrollment" }))
        .send()
        .await
        .expect("organization revalidation request should reach the app");
    assert_eq!(missing_owner.status(), StatusCode::NOT_FOUND);
    assert!(
        server
            .state
            .store
            .list_memberships_by_org("org_created_later")
            .await
            .expect("membership lookup should succeed")
            .is_empty()
    );

    let organization = Organization {
        id: "org_created_later".into(),
        instance_id: instance.id.clone(),
        name: "Created later".into(),
        slug_key: format!("{}:created-later", instance.id),
        status: OrganizationStatus::Active,
        public_metadata: Default::default(),
        created_at: current_time,
    };
    server
        .state
        .store
        .create_organization(&organization)
        .await
        .expect("organization should persist");
    let verified = client
        .post(&url)
        .bearer_auth(secret)
        .json(&json!({ "user_id": "usr_domain_enrollment" }))
        .send()
        .await
        .expect("verified enrollment should reach the app");
    assert_eq!(verified.status(), StatusCode::OK);
    assert_eq!(
        server
            .state
            .store
            .list_memberships_by_org(&organization.id)
            .await
            .expect("membership lookup should succeed")
            .len(),
        1
    );
}

#[tokio::test]
async fn user_organization_list_hides_memberships_for_tombstoned_organizations() {
    let server = TestServer::start().await;
    let instance = create_instance(&server, "hidden-tombstone-membership").await;
    let current_time = now();
    let client = server.client();
    let secret = "sk_hidden_tombstone_membership";
    let api_key = ApiKey {
        id: "key_hidden_tombstone_membership".into(),
        instance_id: instance.id.clone(),
        name: "users test".into(),
        secret_key: sha256_hex(secret),
        scopes: vec!["users".into()],
        created_at: current_time,
        last_used_at: None,
        revoked: false,
    };
    server
        .state
        .store
        .create("api_keys", &api_key.id, &api_key)
        .await
        .expect("users-scoped API key should persist");
    seed_record(
        &server,
        "users",
        "usr_hidden_tombstone_membership",
        &json!({
            "id": "usr_hidden_tombstone_membership",
            "instanceId": instance.id,
            "email": "member@example.test",
            "emailVerified": true,
            "createdAt": current_time,
            "updatedAt": current_time,
        }),
    )
    .await;
    seed_record(
        &server,
        "organizations",
        "org_tombstoned_membership",
        &json!({
            "id": "org_tombstoned_membership",
            "instanceId": instance.id,
            "name": "Deleted team",
            "slugKey": format!("{}:deleted-team", instance.id),
            "status": "deleted",
            "createdAt": current_time,
        }),
    )
    .await;
    seed_record(
        &server,
        "memberships",
        "mem_orphaned_membership",
        &json!({
            "id": "mem_orphaned_membership",
            "instanceId": instance.id,
            "orgId": "org_tombstoned_membership",
            "userId": "usr_hidden_tombstone_membership",
            "memberKey": "org_tombstoned_membership:usr_hidden_tombstone_membership",
            "role": "admin",
            "createdAt": current_time,
        }),
    )
    .await;

    let response = client
        .get(format!(
            "{}/v1/{}/users/usr_hidden_tombstone_membership/organizations",
            server.origin, instance.slug
        ))
        .bearer_auth(secret)
        .send()
        .await
        .expect("organization list request should reach the app");
    assert_eq!(response.status(), StatusCode::OK);
    let organizations: Value = response
        .json()
        .await
        .expect("organization list should be JSON");
    assert_eq!(organizations, json!([]));
}

#[tokio::test]
async fn organization_membership_routes_hide_deleting_scim_rows_but_keep_manual_rows() {
    let server = TestServer::start().await;
    let instance = create_instance(&server, "membership-auth-eligibility").await;
    let current_time = now();
    let client = server.client();
    let secret = "sk_membership_auth_eligibility";
    let api_key = ApiKey {
        id: "key_membership_auth_eligibility".into(),
        instance_id: instance.id.clone(),
        name: "organization membership test".into(),
        secret_key: sha256_hex(secret),
        scopes: vec!["organizations".into(), "users".into(), "domains".into()],
        created_at: current_time,
        last_used_at: None,
        revoked: false,
    };
    server
        .state
        .store
        .create("api_keys", &api_key.id, &api_key)
        .await
        .expect("API key should persist");
    let organization = Organization {
        id: "org_membership_auth_eligibility".into(),
        instance_id: instance.id.clone(),
        name: "Membership eligibility".into(),
        slug_key: format!("{}:membership-eligibility", instance.id),
        status: OrganizationStatus::Active,
        public_metadata: Default::default(),
        created_at: current_time,
    };
    server
        .state
        .store
        .create_organization(&organization)
        .await
        .expect("organization should persist");
    seed_record(
        &server,
        "users",
        "usr_deleting_scim_member",
        &json!({
            "id": "usr_deleting_scim_member",
            "instanceId": instance.id,
            "email": "deleting@example.test",
            "emailKey": format!("{}:deleting@example.test", instance.id),
            "emailVerified": true,
            "createdAt": current_time,
            "updatedAt": current_time,
        }),
    )
    .await;
    seed_record(
        &server,
        "users",
        "usr_manual_member",
        &json!({
            "id": "usr_manual_member",
            "instanceId": instance.id,
            "email": "manual@example.test",
            "emailVerified": true,
            "createdAt": current_time,
            "updatedAt": current_time,
        }),
    )
    .await;
    let stale_scim_membership = OrgMembership {
        id: "mem_deleting_scim_member".into(),
        instance_id: instance.id.clone(),
        org_id: organization.id.clone(),
        user_id: "usr_deleting_scim_member".into(),
        member_key: format!("{}:usr_deleting_scim_member", organization.id),
        role: "admin".into(),
        created_at: current_time,
    };
    server
        .state
        .store
        .create_membership(&stale_scim_membership)
        .await
        .expect("SCIM membership should persist before its provenance claim");
    let manual_membership = OrgMembership {
        id: "mem_manual_member".into(),
        instance_id: instance.id.clone(),
        org_id: organization.id.clone(),
        user_id: "usr_manual_member".into(),
        member_key: format!("{}:usr_manual_member", organization.id),
        role: "member".into(),
        created_at: current_time,
    };
    server
        .state
        .store
        .create_membership(&manual_membership)
        .await
        .expect("manual membership should persist");
    seed_record(
        &server,
        "scim_directories",
        "dir_membership_auth_eligibility",
        &json!({
            "id": "dir_membership_auth_eligibility",
            "instanceId": instance.id,
            "orgId": organization.id,
            "status": "active",
            "tokenKey": "unused-test-token-key",
            "createdAt": current_time,
        }),
    )
    .await;
    seed_record(
        &server,
        "scim_user_links",
        "dir_membership_auth_eligibility:usr_deleting_scim_member",
        &json!({
            "id": "dir_membership_auth_eligibility:usr_deleting_scim_member",
            "instanceId": instance.id,
            "directoryId": "dir_membership_auth_eligibility",
            "userId": "usr_deleting_scim_member",
            "active": true,
            "deleting": true,
        }),
    )
    .await;
    seed_record(
        &server,
        "scim_user_links",
        "dir_membership_auth_eligibility:usr_manual_member",
        &json!({
            "id": "dir_membership_auth_eligibility:usr_manual_member",
            "instanceId": instance.id,
            "directoryId": "dir_membership_auth_eligibility",
            "userId": "usr_manual_member",
            "active": true,
            "deleting": true,
        }),
    )
    .await;
    for (user_id, membership_id) in [
        (
            "usr_deleting_scim_member",
            stale_scim_membership.id.as_str(),
        ),
        ("usr_manual_member", "mem_previous_scim_membership"),
    ] {
        let claim_id = format!("{}:{user_id}", organization.id);
        seed_record(
            &server,
            "scim_membership_claims",
            &claim_id,
            &json!({
                "id": claim_id,
                "instanceId": instance.id,
                "orgId": organization.id,
                "userId": user_id,
                "membershipId": membership_id,
            }),
        )
        .await;
    }
    let domain = OrgDomain {
        id: "domain_membership_auth_eligibility".into(),
        instance_id: instance.id.clone(),
        org_id: organization.id.clone(),
        domain: "example.test".into(),
        domain_key: format!("{}:example.test", instance.id),
        created_at: current_time,
    };
    server
        .state
        .store
        .create("org_domains", &domain.id, &domain)
        .await
        .expect("domain policy should persist");

    let listed = client
        .get(format!(
            "{}/v1/{}/organizations/{}/memberships",
            server.origin, instance.slug, organization.id
        ))
        .bearer_auth(secret)
        .send()
        .await
        .expect("membership list should reach the app");
    assert_eq!(listed.status(), StatusCode::OK);
    let listed: Value = listed.json().await.expect("membership list should be JSON");
    assert_eq!(
        listed
            .as_array()
            .expect("membership list is an array")
            .len(),
        1
    );
    assert_eq!(listed[0]["id"], manual_membership.id);

    let stale_user_orgs = client
        .get(format!(
            "{}/v1/{}/users/{}/organizations",
            server.origin, instance.slug, stale_scim_membership.user_id
        ))
        .bearer_auth(secret)
        .send()
        .await
        .expect("stale user's organization list should reach the app");
    assert_eq!(stale_user_orgs.status(), StatusCode::OK);
    assert_eq!(
        stale_user_orgs
            .json::<Value>()
            .await
            .expect("user organization list should be JSON"),
        json!([])
    );
    let manual_user_orgs = client
        .get(format!(
            "{}/v1/{}/users/{}/organizations",
            server.origin, instance.slug, manual_membership.user_id
        ))
        .bearer_auth(secret)
        .send()
        .await
        .expect("manual user's organization list should reach the app");
    assert_eq!(manual_user_orgs.status(), StatusCode::OK);
    let manual_user_orgs: Value = manual_user_orgs
        .json()
        .await
        .expect("user organization list should be JSON");
    assert_eq!(
        manual_user_orgs.as_array().expect("list is an array").len(),
        1
    );
    assert_eq!(manual_user_orgs[0]["organization"]["id"], organization.id);

    let update = client
        .patch(format!(
            "{}/v1/{}/organizations/{}/memberships/{}",
            server.origin, instance.slug, organization.id, stale_scim_membership.id
        ))
        .bearer_auth(secret)
        .json(&json!({ "role": "owner" }))
        .send()
        .await
        .expect("stale membership update should reach the app");
    assert_eq!(update.status(), StatusCode::NOT_FOUND);
    let delete = client
        .delete(format!(
            "{}/v1/{}/organizations/{}/memberships/{}",
            server.origin, instance.slug, organization.id, stale_scim_membership.id
        ))
        .bearer_auth(secret)
        .send()
        .await
        .expect("stale membership deletion should reach the app");
    assert_eq!(delete.status(), StatusCode::NOT_FOUND);
    let retained: OrgMembership = server
        .state
        .store
        .get("memberships", &stale_scim_membership.id)
        .await
        .expect("retained membership lookup should succeed")
        .expect("retained membership should remain readable");
    assert_eq!(retained.role, "admin");

    let duplicate = client
        .post(format!(
            "{}/v1/{}/organizations/{}/memberships",
            server.origin, instance.slug, organization.id
        ))
        .bearer_auth(secret)
        .json(&json!({
            "user_id": stale_scim_membership.user_id,
            "role": "member"
        }))
        .send()
        .await
        .expect("stale membership create conflict should reach the app");
    assert_eq!(duplicate.status(), StatusCode::CONFLICT);
    let duplicate: Value = duplicate
        .json()
        .await
        .expect("stale membership create conflict should be JSON");
    assert_eq!(duplicate["error"]["code"], "membership_changed");

    let enrollment = client
        .post(format!(
            "{}/v1/{}/domains/example.test/enroll",
            server.origin, instance.slug
        ))
        .bearer_auth(secret)
        .json(&json!({ "user_id": stale_scim_membership.user_id }))
        .send()
        .await
        .expect("stale membership domain enrollment should reach the app");
    assert_eq!(enrollment.status(), StatusCode::CONFLICT);
    let enrollment: Value = enrollment
        .json()
        .await
        .expect("domain enrollment conflict should be JSON");
    assert_eq!(enrollment["error"]["code"], "membership_changed");

    let session_secret = "session_for_scim_membership_invitation_retry";
    let session = Session {
        id: "ses_scim_membership_invitation_retry".into(),
        instance_id: instance.id.clone(),
        user_id: stale_scim_membership.user_id.clone(),
        token_key: sha256_hex(session_secret),
        status: "active".into(),
        auth_epoch: 0,
        created_at: current_time,
        expires_at: current_time + 3_600,
        last_active_at: Some(current_time),
        user_agent: None,
        ip_address: None,
    };
    server
        .state
        .store
        .create("sessions", &session.id, &session)
        .await
        .expect("session should persist");
    let invitation = Invitation {
        id: "inv_scim_membership_retry".into(),
        instance_id: instance.id.clone(),
        org_id: organization.id.clone(),
        email: "deleting@example.test".into(),
        role: "member".into(),
        status: "pending".into(),
        expires_at: current_time + 3_600,
        created_at: current_time,
    };
    server
        .state
        .store
        .create_invitation(&invitation)
        .await
        .expect("invitation should persist");
    let acceptance_token = users::issue_token(
        &server.state,
        &instance.id,
        Some(&invitation.id),
        "invitation",
        3_600,
    )
    .await
    .expect("acceptance token should persist");
    let acceptance_url = format!(
        "{}/ui/{}/invitations/{}/accept",
        server.origin, instance.slug, invitation.id
    );
    let failed_acceptance = client
        .get(&acceptance_url)
        .query(&[("token", acceptance_token.as_str())])
        .header(header::COOKIE, format!("tsauth_session={session_secret}"))
        .send()
        .await
        .expect("stale-membership invitation should reach the app");
    assert_eq!(failed_acceptance.status(), StatusCode::CONFLICT);
    assert_eq!(
        server
            .state
            .store
            .get::<Invitation>("invitations", &invitation.id)
            .await
            .expect("invitation lookup should succeed")
            .expect("invitation should remain retryable")
            .status,
        "accepting"
    );
    assert!(
        !server
            .state
            .store
            .query_one::<VerificationToken>(
                "verification_tokens",
                "tokenKey",
                &sha256_hex(&acceptance_token),
            )
            .await
            .expect("acceptance token lookup should succeed")
            .expect("acceptance token should remain")
            .consumed
    );

    server
        .state
        .store
        .delete_membership(&stale_scim_membership.id)
        .await
        .expect("SCIM cleanup should remove the retained membership");
    let stale_claim_id = format!("{}:{}", organization.id, stale_scim_membership.user_id);
    server
        .state
        .store
        .delete("scim_membership_claims", &stale_claim_id)
        .await
        .expect("SCIM cleanup should remove the durable provenance claim");
    let retried_acceptance = client
        .get(&acceptance_url)
        .query(&[("token", acceptance_token.as_str())])
        .header(header::COOKIE, format!("tsauth_session={session_secret}"))
        .send()
        .await
        .expect("invitation retry should reach the app");
    assert_eq!(retried_acceptance.status(), StatusCode::OK);
    assert!(
        retried_acceptance
            .text()
            .await
            .expect("accepted page should be readable")
            .contains("Invitation accepted")
    );
    let invited_membership_id = format!(
        "mem_inv_{}",
        sha256_hex(&format!(
            "{}:{}",
            invitation.id, stale_scim_membership.user_id
        ))
    );
    let invited_membership: OrgMembership = server
        .state
        .store
        .get("memberships", &invited_membership_id)
        .await
        .expect("invitation membership lookup should succeed")
        .expect("retry should create the invited membership");
    assert!(
        server
            .state
            .store
            .org_membership_auth_eligible(&invited_membership)
            .await
            .expect("invited membership eligibility should be readable")
    );
}

#[tokio::test]
async fn organization_deletion_retry_audits_after_a_failed_cascade() {
    let server = TestServer::start().await;
    let (instance, organization, secret) =
        create_deletable_organization(&server, "cascade-retry").await;
    seed_record(
        &server,
        "scim_membership_claims",
        "malformed_claim",
        &json!({
            "instanceId": instance.id,
            "orgId": organization.id,
            "userId": "usr_orphaned_claim",
        }),
    )
    .await;
    let client = server.client();
    let url = format!(
        "{}/v1/{}/organizations/{}",
        server.origin, instance.slug, organization.id
    );
    let failed = client
        .delete(&url)
        .bearer_auth(&secret)
        .send()
        .await
        .expect("first delete request should reach the app");
    assert_eq!(failed.status(), StatusCode::INTERNAL_SERVER_ERROR);
    assert_eq!(
        server
            .state
            .store
            .get_organization_for_deletion(&organization.id)
            .await
            .expect("organization lookup should succeed")
            .expect("failed cascade leaves its tombstone")
            .status,
        OrganizationStatus::Deleting
    );
    let audit_id = format!("evt_org_deleted_{}", sha256_hex(&organization.id));
    assert!(
        server
            .state
            .store
            .get_audit_event(&audit_id)
            .await
            .expect("audit lookup should succeed")
            .is_none(),
        "failed cascade must not emit the deletion audit"
    );

    server
        .state
        .store
        .delete("scim_membership_claims", "malformed_claim")
        .await
        .expect("malformed claim should be repairable");
    let retried = client
        .delete(&url)
        .bearer_auth(&secret)
        .send()
        .await
        .expect("retry should reach the app");
    assert_eq!(retried.status(), StatusCode::NO_CONTENT);
    let deleted = server
        .state
        .store
        .get_organization_for_deletion(&organization.id)
        .await
        .expect("organization lookup should succeed")
        .expect("deleted organization remains as a tombstone");
    assert_eq!(deleted.status, OrganizationStatus::Deleted);
    let event = server
        .state
        .store
        .get_audit_event(&audit_id)
        .await
        .expect("audit lookup should succeed")
        .expect("successful retry should emit the deletion audit");
    assert_eq!(event.r#type, "organization.deleted");
    assert_eq!(event.instance_id, instance.id);
    assert_eq!(
        event.actor_id.as_deref(),
        Some("key_organization_delete_cascade-retry")
    );
    assert_eq!(event.data["organization_id"], organization.id);
}

#[tokio::test]
async fn organization_deletion_retry_recovers_a_failed_audit_write_once() {
    let server = TestServer::start().await;
    let (instance, organization, secret) =
        create_deletable_organization(&server, "audit-retry").await;
    server
        .storage
        .fail_next_create_only_before_commit("audit_events");
    let client = server.client();
    let url = format!(
        "{}/v1/{}/organizations/{}",
        server.origin, instance.slug, organization.id
    );
    let failed = client
        .delete(&url)
        .bearer_auth(&secret)
        .send()
        .await
        .expect("delete request should reach the app");
    assert_eq!(failed.status(), StatusCode::INTERNAL_SERVER_ERROR);
    assert_eq!(
        server
            .state
            .store
            .get_organization_for_deletion(&organization.id)
            .await
            .expect("organization lookup should succeed")
            .expect("deleted organization remains as a tombstone")
            .status,
        OrganizationStatus::Deleted
    );
    let audit_id = format!("evt_org_deleted_{}", sha256_hex(&organization.id));
    assert!(
        server
            .state
            .store
            .get_audit_event(&audit_id)
            .await
            .expect("audit lookup should succeed")
            .is_none(),
        "failed audit write leaves a retryable tombstone"
    );

    let retried = client
        .delete(&url)
        .bearer_auth(&secret)
        .send()
        .await
        .expect("retry should reach the app");
    assert_eq!(retried.status(), StatusCode::NO_CONTENT);
    let repeated = client
        .delete(&url)
        .bearer_auth(&secret)
        .send()
        .await
        .expect("repeated delete should reach the app");
    assert_eq!(repeated.status(), StatusCode::NO_CONTENT);

    let events: Vec<AuditEvent> = server
        .state
        .store
        .query_all(
            "audit_events",
            &[auth_store::Predicate::equal("instanceId", &instance.id)
                .expect("audit query predicate should serialize")],
            100,
        )
        .await
        .expect("audit event listing should succeed");
    let deleted_events = events
        .iter()
        .filter(|event| event.r#type == "organization.deleted")
        .collect::<Vec<_>>();
    assert_eq!(deleted_events.len(), 1);
    assert_eq!(deleted_events[0].id, audit_id);
    assert_eq!(deleted_events[0].data["organization_id"], organization.id);
}
