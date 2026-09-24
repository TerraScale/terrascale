use super::*;

/// Operational fanout state shares the atomic audit document write, but is not
/// part of the immutable event or its signed/exported representation.
#[derive(Debug, Clone, Serialize, serde::Deserialize)]
#[serde(rename_all = "camelCase")]
struct AuditDispatchRecord {
    #[serde(flatten)]
    event: AuditEvent,
    #[serde(default)]
    dispatch_pending: bool,
}

macro_rules! record_rw {
    ($model:ty, $collection:literal, $get:ident, $upsert:ident) => {
        pub async fn $get(&self, id: &str) -> StoreResult<Option<$model>> {
            self.get($collection, id).await
        }
        pub async fn $upsert(&self, record: &$model) -> StoreResult<()> {
            self.upsert($collection, &record.id, record).await
        }
    };
}
macro_rules! record_create {
    ($model:ty, $collection:literal, $create:ident) => {
        pub async fn $create(&self, record: &$model) -> StoreResult<$model> {
            self.create($collection, &record.id, record).await?;
            Ok(record.clone())
        }
    };
}
macro_rules! record_delete {
    ($collection:literal, $delete:ident) => {
        pub async fn $delete(&self, id: &str) -> StoreResult<()> {
            self.delete($collection, id).await
        }
    };
}
macro_rules! find_one {
    ($model:ty, $collection:literal, $name:ident, $field:literal) => {
        pub async fn $name(&self, key: &str) -> StoreResult<Option<$model>> {
            self.query_one($collection, $field, key).await
        }
    };
}
macro_rules! list_all {
    ($model:ty, $collection:literal, $name:ident, $field:literal, $page:expr) => {
        pub async fn $name(&self, key: &str) -> StoreResult<Vec<$model>> {
            self.query_all($collection, &[Predicate::equal($field, key)?], $page)
                .await
        }
    };
}

impl AuthStore {
    async fn create_with_unique<T: Serialize + DeserializeOwned + Clone>(
        &self,
        collection: &str,
        id: &str,
        record: &T,
        kind: &str,
        key: &str,
        owner: &str,
    ) -> StoreResult<T> {
        if let Err(error) = self.claim_unique(kind, key, owner).await {
            // A prior attempt with this same durable document ID may have
            // claimed the name before its create-only document write failed.
            let claim_id = unique_claim_id(kind, key);
            let claim_matches =
                self.get::<Value>("uniques", &claim_id)
                    .await?
                    .is_some_and(|claim| {
                        claim.get("ownerType").and_then(Value::as_str) == Some(kind)
                            && claim.get("ownerId").and_then(Value::as_str) == Some(owner)
                    });
            if !claim_matches {
                return Err(error);
            }
        }
        match self.create(collection, id, record).await {
            Ok(()) => Ok(record.clone()),
            Err(error) => {
                // A timeout can arrive after the document committed. Read back
                // the exact ID before deciding whether this retry succeeded.
                match self.get::<T>(collection, id).await {
                    Ok(Some(existing))
                        if serde_json::to_value(&existing)? == serde_json::to_value(record)? =>
                    {
                        return Ok(existing);
                    }
                    Ok(None) if error.is_conflict() => {
                        self.release_unique_if_owned(kind, key, owner).await?;
                    }
                    // On transport/server ambiguity even a missing immediate
                    // read cannot prove a delayed write will not finish later.
                    _ => {}
                }
                Err(error)
            }
        }
    }

    pub async fn create_instance(&self, record: &Instance) -> StoreResult<Instance> {
        self.create_with_unique(
            "instances",
            &record.id,
            record,
            "instance-slug",
            &record.slug,
            &record.id,
        )
        .await
    }
    record_rw!(Instance, "instances", get_instance, upsert_instance);
    find_one!(Instance, "instances", find_instance_by_slug, "slug");
    pub async fn create_user(&self, user: &User) -> StoreResult<User> {
        if user.scim_provisioning.is_some() && !user.deleted {
            self.release_quota_seat_for_user(user).await?;
            return Err(StoreError::InvalidConfiguration(
                "a provisional SCIM user must be hidden until activation".into(),
            ));
        }
        if let Some(email) = &user.email_key
            && let Err(error) = self.claim_unique("user-email", email, &user.id).await
        {
            if error.is_conflict() {
                let claim: Option<Value> = self
                    .get("uniques", &unique_claim_id("user-email", email))
                    .await?;
                if claim
                    .as_ref()
                    .and_then(|claim| claim.get("ownerId"))
                    .and_then(Value::as_str)
                    != Some(user.id.as_str())
                {
                    self.release_quota_seat_for_user(user).await?;
                }
            }
            return Err(error);
        }
        if let Some(username) = &user.username_key
            && let Err(error) = self.claim_unique("user-username", username, &user.id).await
        {
            let own_claim = if error.is_conflict() {
                let claim: Option<Value> = self
                    .get("uniques", &unique_claim_id("user-username", username))
                    .await?;
                claim
                    .as_ref()
                    .and_then(|claim| claim.get("ownerId"))
                    .and_then(Value::as_str)
                    == Some(user.id.as_str())
            } else {
                true
            };
            if own_claim {
                return Err(error);
            }
            if let Some(email) = &user.email_key {
                self.release_unique_if_owned("user-email", email, &user.id)
                    .await?;
            }
            self.release_quota_seat_for_user(user).await?;
            return Err(error);
        }
        if let Err(error) = self.activate_quota_seat_for_user(user).await {
            if error.is_precondition() {
                self.compensate_failed_user_create(user).await?;
            }
            return Err(error);
        }
        match self.create("users", &user.id, user).await {
            Ok(()) => {
                self.finalize_quota_admission(&user.instance_id, &user.id)
                    .await?;
                Ok(user.clone())
            }
            Err(error) => {
                match self.get::<User>("users", &user.id).await {
                    Ok(Some(existing))
                        if serde_json::to_value(&existing)? == serde_json::to_value(user)? =>
                    {
                        self.finalize_quota_admission(&user.instance_id, &user.id)
                            .await?;
                        return Ok(existing);
                    }
                    Ok(None) if error.is_conflict() => {
                        self.compensate_failed_user_create(user).await?;
                    }
                    // On transport/server ambiguity, even a missing immediate read
                    // cannot prove the original write will not finish later.
                    _ => {}
                }
                Err(error)
            }
        }
    }
    async fn compensate_failed_user_create(&self, user: &User) -> StoreResult<()> {
        if let Some(email) = &user.email_key {
            self.release_unique_if_owned("user-email", email, &user.id)
                .await?;
        }
        if let Some(username) = &user.username_key {
            self.release_unique_if_owned("user-username", username, &user.id)
                .await?;
        }
        self.release_quota_seat_for_user(user).await
    }
    pub async fn get_user(&self, id: &str) -> StoreResult<Option<User>> {
        Ok(self
            .get::<User>("users", id)
            .await?
            .filter(|user| user.scim_provisioning.is_none()))
    }
    async fn scim_activation_link(
        &self,
        user: &User,
        directory_id: &str,
    ) -> StoreResult<Option<ScimUserLink>> {
        let link_id = format!("{directory_id}:{}", user.id);
        Ok(self
            .get::<ScimUserLink>("scim_user_links", &link_id)
            .await?
            .filter(|link| {
                link.id == link_id
                    && !link.deleting
                    && !link.cleanup_complete
                    && link.user_id == user.id
                    && link.directory_id == directory_id
                    && link.instance_id == user.instance_id
            }))
    }
    async fn scim_activation_eligible(&self, user: &User, directory_id: &str) -> StoreResult<bool> {
        let Some(directory) = self
            .get::<ScimDirectory>("scim_directories", directory_id)
            .await?
        else {
            return Ok(false);
        };
        if directory.status != ScimDirectoryStatus::Active
            || directory.instance_id != user.instance_id
            || directory.org_id.is_none() && !directory.instance_wide
        {
            return Ok(false);
        }
        if let Some(org_id) = &directory.org_id
            && !self
                .organization_is_active(org_id, &user.instance_id)
                .await?
        {
            return Ok(false);
        }
        let Some(link) = self.scim_activation_link(user, directory_id).await? else {
            return Ok(false);
        };
        if link.active
            && let Some(org_id) = &directory.org_id
        {
            let membership: Option<OrgMembership> = self
                .query_one("memberships", "memberKey", &format!("{org_id}:{}", user.id))
                .await?;
            if !membership.is_some_and(|member| {
                member.org_id == *org_id
                    && member.user_id == user.id
                    && member.instance_id == user.instance_id
            }) {
                return Ok(false);
            }
        }
        Ok(true)
    }
    /// Commit visibility only after the directory link and its active
    /// organization membership are durable. The User CAS is the linearization
    /// point against exact-owner compensation.
    pub async fn activate_scim_user(
        &self,
        user_id: &str,
        directory_id: &str,
    ) -> StoreResult<ScimActivationOutcome> {
        for _ in 0..8 {
            let Some((mut user, version)) = self.get_versioned::<User>("users", user_id).await?
            else {
                return Ok(ScimActivationOutcome::NoLongerEligible);
            };
            if !user.deleted && user.scim_provisioning.is_none() {
                return Ok(
                    if self
                        .scim_activation_link(&user, directory_id)
                        .await?
                        .is_some()
                    {
                        ScimActivationOutcome::AlreadyActiveOwned
                    } else {
                        ScimActivationOutcome::NoLongerEligible
                    },
                );
            }
            if !user.deleted
                || !user.scim_provisioning.as_ref().is_some_and(|provisioning| {
                    provisioning.directory_id == directory_id
                        && provisioning.phase == ScimProvisioningPhase::Pending
                })
                || !self.scim_activation_eligible(&user, directory_id).await?
            {
                return Ok(ScimActivationOutcome::NoLongerEligible);
            }
            user.deleted = false;
            user.scim_provisioning = None;
            user.identity_established = true;
            if self
                .get::<ScimDirectory>("scim_directories", directory_id)
                .await?
                .is_some_and(|directory| directory.instance_wide)
                && !user
                    .scim_instance_wide_directory_ids
                    .iter()
                    .any(|id| id == directory_id)
            {
                user.scim_instance_wide_directory_ids
                    .push(directory_id.to_owned());
            }
            match self
                .compare_and_put("users", user_id, &user, &version)
                .await
            {
                Ok(true) => return Ok(ScimActivationOutcome::Activated),
                Ok(false) => {}
                Err(error) => {
                    if self
                        .get::<User>("users", user_id)
                        .await
                        .ok()
                        .flatten()
                        .is_some_and(|current| {
                            !current.deleted
                                && current.scim_provisioning.is_none()
                                && current.instance_id == user.instance_id
                                && current.scim_instance_wide_directory_ids
                                    == user.scim_instance_wide_directory_ids
                        })
                        && self
                            .scim_activation_link(&user, directory_id)
                            .await?
                            .is_some()
                    {
                        return Ok(ScimActivationOutcome::AlreadyActiveOwned);
                    }
                    // A missing readback cannot rule out a delayed commit.
                    return Err(error);
                }
            }
        }
        Err(StoreError::Contention)
    }
    /// Remove only this directory's still-hidden provisional identity. A
    /// successful activation changes the User version and makes deletion fail.
    pub async fn compensate_provisional_scim_user(
        &self,
        user_id: &str,
        directory_id: &str,
    ) -> StoreResult<bool> {
        for _ in 0..8 {
            let Some((mut user, version)) = self.get_versioned::<User>("users", user_id).await?
            else {
                return Ok(false);
            };
            let Some(provisioning) = &user.scim_provisioning else {
                return Ok(false);
            };
            if !user.deleted || provisioning.directory_id != directory_id {
                return Ok(false);
            }
            if !self.scim_provisional_has_children(user_id).await? {
                if provisioning.phase == ScimProvisioningPhase::Pending {
                    user.scim_provisioning = Some(ScimProvisioning {
                        directory_id: directory_id.to_owned(),
                        phase: ScimProvisioningPhase::Cleaning,
                    });
                    match self
                        .compare_and_put("users", user_id, &user, &version)
                        .await
                    {
                        Ok(_) => continue,
                        Err(error) => {
                            let fenced =
                                self.get::<User>("users", user_id)
                                    .await?
                                    .is_some_and(|current| {
                                        current.deleted
                                            && current.scim_provisioning.is_some_and(|state| {
                                                state.directory_id == directory_id
                                                    && state.phase
                                                        == ScimProvisioningPhase::Cleaning
                                            })
                                    });
                            if fenced {
                                continue;
                            }
                            return Err(error);
                        }
                    }
                }
                if let Some(email_key) = &user.email_key {
                    self.release_unique_if_owned("user-email", email_key, user_id)
                        .await?;
                }
                if let Some(username_key) = &user.username_key {
                    self.release_unique_if_owned("user-username", username_key, user_id)
                        .await?;
                }
                self.release_quota_seat_for_user(&user).await?;
                if self.scim_provisional_has_children(user_id).await? {
                    return Ok(false);
                }
                match self.compare_and_delete("users", user_id, &version).await {
                    Ok(true) => return Ok(true),
                    Ok(false) => continue,
                    Err(error) => {
                        if self.get::<User>("users", user_id).await?.is_none() {
                            return Ok(true);
                        }
                        return Err(error);
                    }
                }
            }
            return Ok(false);
        }
        Err(StoreError::Contention)
    }
    async fn scim_provisional_has_children(&self, user_id: &str) -> StoreResult<bool> {
        Ok(!self
            .query_all::<ScimUserLink>(
                "scim_user_links",
                &[Predicate::equal("userId", user_id)?],
                100,
            )
            .await?
            .is_empty()
            || !self
                .query_all::<OrgMembership>(
                    "memberships",
                    &[Predicate::equal("userId", user_id)?],
                    100,
                )
                .await?
                .is_empty())
    }
    /// Record an instance-wide directory before its link is written so a
    /// failed link write leaves authentication denied until it is repaired.
    pub async fn attach_instance_wide_scim_directory(
        &self,
        user_id: &str,
        directory_id: &str,
    ) -> StoreResult<bool> {
        for _ in 0..8 {
            let Some((mut user, version)) = self.get_versioned::<User>("users", user_id).await?
            else {
                return Ok(false);
            };
            if user.deleted || user.scim_provisioning.is_some() {
                return Ok(false);
            }
            let Some(directory) = self
                .get::<ScimDirectory>("scim_directories", directory_id)
                .await?
            else {
                return Ok(false);
            };
            if directory.status != ScimDirectoryStatus::Active
                || !directory.instance_wide
                || directory.instance_id != user.instance_id
            {
                return Ok(false);
            }
            if user
                .scim_instance_wide_directory_ids
                .iter()
                .any(|id| id == directory_id)
            {
                return Ok(true);
            }
            user.scim_instance_wide_directory_ids
                .push(directory_id.to_owned());
            if self
                .compare_and_put("users", user_id, &user, &version)
                .await?
            {
                return Ok(true);
            }
        }
        Err(StoreError::Contention)
    }
    /// Recheck durable User and directory/link state at authentication time.
    pub async fn scim_auth_eligible(&self, user: &User) -> StoreResult<bool> {
        let Some(current) = self.get::<User>("users", &user.id).await? else {
            return Ok(false);
        };
        if current.instance_id != user.instance_id
            || current.deleted
            || current.scim_provisioning.is_some()
            || current.banned
        {
            return Ok(false);
        }
        for directory_id in &current.scim_instance_wide_directory_ids {
            let Some(directory) = self
                .get::<ScimDirectory>("scim_directories", directory_id)
                .await?
            else {
                return Ok(false);
            };
            match directory.status {
                ScimDirectoryStatus::Deleted => continue,
                ScimDirectoryStatus::Deleting => return Ok(false),
                ScimDirectoryStatus::Active => {}
            }
            if !directory.instance_wide
                || directory.instance_id != current.instance_id
                || !self
                    .scim_activation_link(&current, directory_id)
                    .await?
                    .is_some_and(|link| link.active)
            {
                return Ok(false);
            }
        }
        Ok(true)
    }
    pub async fn upsert_user(&self, record: &User) -> StoreResult<()> {
        if self
            .rekey_user(
                &record.id,
                &record.instance_id,
                record.email_key.clone(),
                record.username_key.clone(),
                |current| *current = record.clone(),
            )
            .await?
        {
            Ok(())
        } else {
            Err(StoreError::Precondition)
        }
    }
    pub async fn create_passkey(&self, record: &Passkey) -> StoreResult<Passkey> {
        self.create_with_unique(
            "passkeys",
            &record.id,
            record,
            "passkey",
            &record.credential_key,
            &record.id,
        )
        .await
    }
    record_rw!(Passkey, "passkeys", get_passkey, upsert_passkey);
    find_one!(
        Passkey,
        "passkeys",
        find_passkey_by_credential_id,
        "credentialKey"
    );
    list_all!(Passkey, "passkeys", list_passkeys, "userId", 100);
    pub async fn delete_passkey(&self, id: &str) -> StoreResult<()> {
        let record = self.get_passkey(id).await?;
        self.delete_ignoring_missing("passkeys", id).await?;
        if let Some(record) = record {
            self.release_unique_if_owned("passkey", &record.credential_key, &record.id)
                .await?;
        }
        Ok(())
    }
    record_rw!(Session, "sessions", get_session, upsert_session);
    record_create!(Session, "sessions", create_session);
    find_one!(Session, "sessions", find_session_by_token, "tokenKey");
    list_all!(Session, "sessions", list_sessions_by_user, "userId", 100);
    record_rw!(
        OAuthClient,
        "oauth_clients",
        get_oauth_client,
        upsert_oauth_client
    );
    pub async fn create_oauth_client(&self, record: &OAuthClient) -> StoreResult<OAuthClient> {
        self.create_with_unique(
            "oauth_clients",
            &record.id,
            record,
            "oauth-client",
            &record.client_key,
            &record.id,
        )
        .await
    }
    pub async fn delete_oauth_client(&self, id: &str) -> StoreResult<()> {
        let record = self.get_oauth_client(id).await?;
        self.delete_ignoring_missing("oauth_clients", id).await?;
        if let Some(record) = record {
            self.release_unique_if_owned("oauth-client", &record.client_key, &record.id)
                .await?;
        }
        Ok(())
    }
    record_rw!(
        AuthorizationCode,
        "oauth_codes",
        get_authorization_code,
        upsert_authorization_code
    );
    record_create!(AuthorizationCode, "oauth_codes", create_authorization_code);
    find_one!(
        AuthorizationCode,
        "oauth_codes",
        find_authorization_code,
        "codeKey"
    );
    record_rw!(
        RefreshToken,
        "refresh_tokens",
        get_refresh_token,
        upsert_refresh_token
    );
    pub async fn create_refresh_token(&self, token: &RefreshToken) -> StoreResult<RefreshToken> {
        if token.family_id.is_empty() {
            return Err(StoreError::InvalidConfiguration(
                "refresh family ID is required".into(),
            ));
        }
        if self
            .is_refresh_family_revoked(&token.instance_id, &token.client_id, &token.family_id)
            .await?
        {
            return Err(StoreError::Precondition);
        }
        self.create("refresh_tokens", &token.id, token).await?;
        if self
            .is_refresh_family_revoked(&token.instance_id, &token.client_id, &token.family_id)
            .await?
        {
            let _ = self
                .try_update_refresh_token(&token.id, |current| {
                    if current.status != "active" {
                        return false;
                    }
                    current.status = "revoked".into();
                    true
                })
                .await?;
            return Err(StoreError::Precondition);
        }
        Ok(token.clone())
    }
    find_one!(
        RefreshToken,
        "refresh_tokens",
        find_refresh_token,
        "tokenKey"
    );
    list_all!(
        RefreshToken,
        "refresh_tokens",
        list_refresh_tokens_by_user,
        "userId",
        100
    );
    record_rw!(ApiKey, "api_keys", get_api_key, upsert_api_key);
    record_create!(ApiKey, "api_keys", create_api_key);
    find_one!(ApiKey, "api_keys", find_api_key, "secretKey");
    list_all!(ApiKey, "api_keys", list_api_keys, "instanceId", 100);
    record_rw!(MfaFactor, "mfa_factors", get_mfa_factor, upsert_mfa_factor);
    record_create!(MfaFactor, "mfa_factors", create_mfa_factor);
    record_delete!("mfa_factors", delete_mfa_factor);
    list_all!(MfaFactor, "mfa_factors", list_mfa_factors, "userId", 100);
    pub async fn get_organization(&self, id: &str) -> StoreResult<Option<Organization>> {
        Ok(self
            .get::<Organization>("organizations", id)
            .await?
            .filter(|org| org.status == OrganizationStatus::Active))
    }
    /// Includes tombstones so a scoped DELETE can resume an interrupted sweep.
    pub async fn get_organization_for_deletion(
        &self,
        id: &str,
    ) -> StoreResult<Option<Organization>> {
        self.get("organizations", id).await
    }
    pub async fn upsert_organization(&self, record: &Organization) -> StoreResult<()> {
        self.upsert("organizations", &record.id, record).await
    }
    async fn organization_is_active(&self, id: &str, instance_id: &str) -> StoreResult<bool> {
        Ok(self
            .get_organization(id)
            .await?
            .is_some_and(|org| org.instance_id == instance_id))
    }
    async fn mark_organization_deleting(&self, id: &str) -> StoreResult<Option<Organization>> {
        for _ in 0..8 {
            let Some((mut org, version)) = self
                .get_versioned::<Organization>("organizations", id)
                .await?
            else {
                return Ok(None);
            };
            if org.status != OrganizationStatus::Active {
                return Ok(Some(org));
            }
            org.status = OrganizationStatus::Deleting;
            if self
                .compare_and_put("organizations", id, &org, &version)
                .await?
            {
                return Ok(Some(org));
            }
        }
        Err(StoreError::Contention)
    }
    pub async fn create_organization(&self, record: &Organization) -> StoreResult<Organization> {
        self.create_with_unique(
            "organizations",
            &record.id,
            record,
            "org-slug",
            &record.slug_key,
            &record.id,
        )
        .await
    }
    pub async fn delete_organization(&self, id: &str) -> StoreResult<()> {
        let Some(org) = self.mark_organization_deleting(id).await? else {
            return Ok(());
        };
        loop {
            let memberships = self.list_memberships_by_org(id).await?;
            if memberships.is_empty() {
                break;
            }
            for member in memberships {
                self.delete_membership(&member.id).await?;
            }
        }
        loop {
            let domains: Vec<_> = self
                .list_org_domains(&org.instance_id)
                .await?
                .into_iter()
                .filter(|domain| domain.org_id == id)
                .collect();
            if domains.is_empty() {
                break;
            }
            for domain in domains {
                self.delete_org_domain(&domain.id).await?;
            }
        }
        loop {
            let invitations = self.list_invitations(id).await?;
            if invitations.is_empty() {
                break;
            }
            for invitation in invitations {
                self.delete_ignoring_missing("invitations", &invitation.id)
                    .await?;
            }
        }
        for directory in self
            .list_scim_directories(&org.instance_id)
            .await?
            .into_iter()
            .filter(|directory| directory.org_id.as_deref() == Some(id))
        {
            self.delete_scim_directory(&directory.id).await?;
        }
        loop {
            let claims: Vec<Value> = self
                .query_all(
                    "scim_membership_claims",
                    &[Predicate::equal("orgId", id)?],
                    100,
                )
                .await?;
            if claims.is_empty() {
                break;
            }
            for claim in claims {
                let claim_id = claim.get("id").and_then(Value::as_str).ok_or_else(|| {
                    StoreError::InvalidConfiguration(
                        "SCIM membership claim omitted id during organization cleanup".into(),
                    )
                })?;
                self.delete_ignoring_missing("scim_membership_claims", claim_id)
                    .await?;
            }
        }
        for _ in 0..8 {
            let Some((mut current, version)) = self
                .get_versioned::<Organization>("organizations", id)
                .await?
            else {
                return Ok(());
            };
            if current.status == OrganizationStatus::Deleted {
                return Ok(());
            }
            current.status = OrganizationStatus::Deleted;
            if self
                .compare_and_put("organizations", id, &current, &version)
                .await?
            {
                return Ok(());
            }
        }
        Err(StoreError::Contention)
    }
    pub async fn list_organizations(&self, instance_id: &str) -> StoreResult<Vec<Organization>> {
        Ok(self
            .query_all::<Organization>(
                "organizations",
                &[Predicate::equal("instanceId", instance_id)?],
                100,
            )
            .await?
            .into_iter()
            .filter(|org| org.status == OrganizationStatus::Active)
            .collect())
    }
    record_rw!(
        OrgMembership,
        "memberships",
        get_membership,
        upsert_membership
    );
    pub async fn create_membership(&self, record: &OrgMembership) -> StoreResult<OrgMembership> {
        self.create_membership_inner(record, None).await
    }
    pub async fn create_membership_for_scim_directory(
        &self,
        record: &OrgMembership,
        directory_id: &str,
    ) -> StoreResult<OrgMembership> {
        self.create_membership_inner(record, Some(directory_id))
            .await
    }
    async fn membership_write_allowed(
        &self,
        record: &OrgMembership,
        directory_id: Option<&str>,
    ) -> StoreResult<bool> {
        if !self
            .organization_is_active(&record.org_id, &record.instance_id)
            .await?
        {
            return Ok(false);
        }
        if let Some(directory_id) = directory_id {
            let Some(directory) = self
                .get::<ScimDirectory>("scim_directories", directory_id)
                .await?
            else {
                return Ok(false);
            };
            if directory.status != ScimDirectoryStatus::Active
                || directory.instance_id != record.instance_id
                || directory.org_id.as_deref() != Some(record.org_id.as_str())
            {
                return Ok(false);
            }
        }
        let Some(user) = self.get::<User>("users", &record.user_id).await? else {
            return Ok(false);
        };
        if user.instance_id != record.instance_id {
            return Ok(false);
        }
        if let Some(directory_id) = directory_id
            && !self
                .scim_activation_link(&user, directory_id)
                .await?
                .is_some_and(|link| link.active)
        {
            return Ok(false);
        }
        Ok(if let Some(provisioning) = &user.scim_provisioning {
            user.deleted
                && provisioning.phase == ScimProvisioningPhase::Pending
                && directory_id == Some(provisioning.directory_id.as_str())
        } else {
            !user.deleted
        })
    }
    /// A retained SCIM provenance claim controls only the membership ID that
    /// SCIM created. Manual memberships remain independent of that claim.
    pub async fn org_membership_auth_eligible(
        &self,
        membership: &OrgMembership,
    ) -> StoreResult<bool> {
        if !self
            .organization_is_active(&membership.org_id, &membership.instance_id)
            .await?
        {
            return Ok(false);
        }
        let claim_id = format!("{}:{}", membership.org_id, membership.user_id);
        let Some(claim) = self
            .get::<Value>("scim_membership_claims", &claim_id)
            .await?
        else {
            return Ok(true);
        };
        if claim.get("instanceId").and_then(Value::as_str) != Some(membership.instance_id.as_str())
            || claim.get("orgId").and_then(Value::as_str) != Some(membership.org_id.as_str())
            || claim.get("userId").and_then(Value::as_str) != Some(membership.user_id.as_str())
        {
            return Ok(false);
        }
        if claim.get("membershipId").and_then(Value::as_str) != Some(membership.id.as_str()) {
            return Ok(true);
        }
        let links: Vec<ScimUserLink> = self
            .query_all(
                "scim_user_links",
                &[Predicate::equal("userId", &membership.user_id)?],
                100,
            )
            .await?;
        for link in links {
            if !link.active
                || link.deleting
                || link.cleanup_complete
                || link.user_id != membership.user_id
                || link.instance_id != membership.instance_id
                || link.id != format!("{}:{}", link.directory_id, link.user_id)
            {
                continue;
            }
            if let Some(directory) = self
                .get::<ScimDirectory>("scim_directories", &link.directory_id)
                .await?
                && directory.status == ScimDirectoryStatus::Active
                && directory.instance_id == membership.instance_id
                && directory.org_id.as_deref() == Some(membership.org_id.as_str())
            {
                return Ok(true);
            }
        }
        Ok(false)
    }
    async fn create_membership_inner(
        &self,
        record: &OrgMembership,
        directory_id: Option<&str>,
    ) -> StoreResult<OrgMembership> {
        if !self.membership_write_allowed(record, directory_id).await? {
            return Err(StoreError::Precondition);
        }
        let result = self
            .create_with_unique(
                "memberships",
                &record.id,
                record,
                "membership",
                &record.member_key,
                &record.id,
            )
            .await;
        if !self.membership_write_allowed(record, directory_id).await? {
            if result.is_ok() {
                self.delete_membership(&record.id).await?;
            }
            return Err(StoreError::Precondition);
        }
        result
    }
    pub async fn delete_membership(&self, id: &str) -> StoreResult<()> {
        let record = self.get_membership(id).await?;
        self.delete_ignoring_missing("memberships", id).await?;
        if let Some(record) = record {
            self.release_unique_if_owned("membership", &record.member_key, &record.id)
                .await?;
        }
        Ok(())
    }
    list_all!(
        OrgMembership,
        "memberships",
        list_memberships_by_user,
        "userId",
        100
    );
    list_all!(
        OrgMembership,
        "memberships",
        list_memberships_by_org,
        "orgId",
        100
    );
    record_rw!(Invitation, "invitations", get_invitation, upsert_invitation);
    pub async fn create_invitation(&self, record: &Invitation) -> StoreResult<Invitation> {
        if !self
            .organization_is_active(&record.org_id, &record.instance_id)
            .await?
        {
            return Err(StoreError::Precondition);
        }
        self.create("invitations", &record.id, record).await?;
        if !self
            .organization_is_active(&record.org_id, &record.instance_id)
            .await?
        {
            self.delete_ignoring_missing("invitations", &record.id)
                .await?;
            return Err(StoreError::Precondition);
        }
        Ok(record.clone())
    }
    list_all!(Invitation, "invitations", list_invitations, "orgId", 100);
    record_rw!(
        VerificationToken,
        "verification_tokens",
        get_verification_token,
        upsert_verification_token
    );
    record_create!(
        VerificationToken,
        "verification_tokens",
        create_verification_token
    );
    find_one!(
        VerificationToken,
        "verification_tokens",
        find_verification_token,
        "tokenKey"
    );
    record_rw!(
        WebhookEndpoint,
        "webhooks",
        get_webhook_endpoint,
        upsert_webhook_endpoint
    );
    record_create!(WebhookEndpoint, "webhooks", create_webhook_endpoint);
    record_delete!("webhooks", delete_webhook_endpoint);
    list_all!(
        WebhookEndpoint,
        "webhooks",
        list_webhook_endpoints,
        "instanceId",
        100
    );
    record_rw!(
        WebhookCursor,
        "webhook_cursors",
        get_webhook_cursor,
        upsert_webhook_cursor
    );
    record_rw!(
        WebhookDelivery,
        "webhook_deliveries",
        get_webhook_delivery,
        upsert_webhook_delivery
    );
    list_all!(
        WebhookDelivery,
        "webhook_deliveries",
        list_webhook_deliveries,
        "instanceId",
        100
    );
    pub async fn get_audit_event(&self, id: &str) -> StoreResult<Option<AuditEvent>> {
        self.get("audit_events", id).await
    }
    pub async fn create_audit_event(&self, event: &AuditEvent) -> StoreResult<AuditEvent> {
        let mut sealed = event.clone();
        let hash = audit_hash(event)?;
        sealed.integrity_version = Some(1);
        sealed.integrity_hash = Some(hash.clone());
        sealed.integrity_signature = self
            .audit_signing_key
            .as_ref()
            .map(|key| audit_signature(key, &hash));
        sealed.integrity_key_fingerprint = self
            .audit_signing_key
            .as_ref()
            .map(|key| format!("{:x}", Sha256::digest(key)));
        self.create(
            "audit_events",
            &sealed.id,
            &AuditDispatchRecord {
                event: sealed.clone(),
                dispatch_pending: true,
            },
        )
        .await?;
        Ok(sealed)
    }

    /// Enumerate durable fanout work without a timestamp cursor. The marker is
    /// written with the audit event, so delayed commits remain discoverable.
    pub async fn list_pending_audit_events(
        &self,
        instance_id: &str,
    ) -> StoreResult<Vec<AuditEvent>> {
        self.list_pending_audit_events_from(instance_id, None).await
    }

    /// Apply a fixed endpoint-lifetime lower bound, never a moving dispatch
    /// cursor. An eligible event committed late stays pending and discoverable.
    pub async fn list_pending_audit_events_since(
        &self,
        instance_id: &str,
        from_created_at: i64,
    ) -> StoreResult<Vec<AuditEvent>> {
        self.list_pending_audit_events_from(instance_id, Some(from_created_at))
            .await
    }

    async fn list_pending_audit_events_from(
        &self,
        instance_id: &str,
        from_created_at: Option<i64>,
    ) -> StoreResult<Vec<AuditEvent>> {
        let mut predicates = vec![
            Predicate::equal("instanceId", instance_id)?,
            Predicate::equal("dispatchPending", true)?,
        ];
        if let Some(since) = from_created_at {
            predicates.push(Predicate::greater_than_or_equal("createdAt", since)?);
        }
        let mut events: Vec<AuditDispatchRecord> =
            self.query_all("audit_events", &predicates, 1000).await?;
        events.sort_by(|left, right| {
            (left.event.created_at, &left.event.id).cmp(&(right.event.created_at, &right.event.id))
        });
        Ok(events.into_iter().map(|record| record.event).collect())
    }

    /// Call only after every deterministic delivery job for this event has
    /// been persisted. A concurrent dispatcher can safely repeat the fanout.
    pub async fn complete_audit_event_dispatch(&self, id: &str) -> StoreResult<bool> {
        for _ in 0..8 {
            let Some((mut record, version)) = self
                .get_versioned::<AuditDispatchRecord>("audit_events", id)
                .await?
            else {
                return Ok(false);
            };
            if !record.dispatch_pending {
                return Ok(true);
            }
            record.dispatch_pending = false;
            if self
                .compare_and_put("audit_events", id, &record, &version)
                .await?
            {
                return Ok(true);
            }
        }
        Err(StoreError::Contention)
    }
    record_rw!(
        SigningKeyRecord,
        "signing_keys",
        get_signing_key,
        upsert_signing_key
    );
    record_create!(SigningKeyRecord, "signing_keys", create_signing_key);
    list_all!(
        SigningKeyRecord,
        "signing_keys",
        list_signing_keys,
        "instanceId",
        100
    );
    record_rw!(Connection, "connections", get_connection, upsert_connection);
    record_create!(Connection, "connections", create_connection);
    record_delete!("connections", delete_connection);
    /// Reapply a bounded patch to the latest connection version. A missing
    /// document is never recreated by a stale administrative update.
    pub async fn update_connection<F>(&self, id: &str, apply: F) -> StoreResult<Option<Connection>>
    where
        F: Fn(&mut Connection),
    {
        for _ in 0..8 {
            let Some((mut connection, version)) =
                self.get_versioned::<Connection>("connections", id).await?
            else {
                return Ok(None);
            };
            let instance_id = connection.instance_id.clone();
            apply(&mut connection);
            if connection.id != id || connection.instance_id != instance_id {
                return Err(StoreError::InvalidConfiguration(
                    "connection identity cannot change through update".into(),
                ));
            }
            if self
                .compare_and_put("connections", id, &connection, &version)
                .await?
            {
                return Ok(Some(connection));
            }
        }
        Err(StoreError::Contention)
    }
    list_all!(
        Connection,
        "connections",
        list_connections,
        "instanceId",
        100
    );
    record_rw!(
        ScimDirectory,
        "scim_directories",
        get_scim_directory,
        upsert_scim_directory
    );
    pub async fn create_scim_directory(
        &self,
        record: &ScimDirectory,
    ) -> StoreResult<ScimDirectory> {
        if let Some(org_id) = &record.org_id
            && !self
                .organization_is_active(org_id, &record.instance_id)
                .await?
        {
            return Err(StoreError::Precondition);
        }
        let result = self
            .create_with_unique(
                "scim_directories",
                &record.id,
                record,
                "scim-token",
                &record.token_key,
                &record.id,
            )
            .await;
        if let Some(org_id) = &record.org_id
            && !self
                .organization_is_active(org_id, &record.instance_id)
                .await?
        {
            if result.is_ok() {
                self.delete_scim_directory(&record.id).await?;
            }
            return Err(StoreError::Precondition);
        }
        result
    }
    pub async fn delete_scim_directory(&self, id: &str) -> StoreResult<()> {
        let mut record = None;
        for _ in 0..8 {
            let Some((mut current, version)) = self
                .get_versioned::<ScimDirectory>("scim_directories", id)
                .await?
            else {
                return Ok(());
            };
            if current.status != ScimDirectoryStatus::Active {
                record = Some(current);
                break;
            }
            current.status = ScimDirectoryStatus::Deleting;
            if self
                .compare_and_put("scim_directories", id, &current, &version)
                .await?
            {
                record = Some(current);
                break;
            }
        }
        let record = record.ok_or(StoreError::Contention)?;
        loop {
            let links: Vec<ScimUserLink> = self
                .query_all(
                    "scim_user_links",
                    &[Predicate::equal("directoryId", id)?],
                    100,
                )
                .await?;
            if links.is_empty() {
                break;
            }
            for link in links {
                self.delete_ignoring_missing("scim_user_links", &link.id)
                    .await?;
            }
        }
        loop {
            let groups: Vec<Value> = self
                .query_all("scim_groups", &[Predicate::equal("directoryId", id)?], 100)
                .await?;
            if groups.is_empty() {
                break;
            }
            for group in groups {
                let group_id = group.get("id").and_then(Value::as_str).ok_or_else(|| {
                    StoreError::InvalidConfiguration(
                        "SCIM group omitted id during directory cleanup".into(),
                    )
                })?;
                self.delete_ignoring_missing("scim_groups", group_id)
                    .await?;
            }
        }
        let mut completed = false;
        for _ in 0..8 {
            let Some((mut current, version)) = self
                .get_versioned::<ScimDirectory>("scim_directories", id)
                .await?
            else {
                return Ok(());
            };
            if current.status == ScimDirectoryStatus::Deleted {
                completed = true;
                break;
            }
            current.status = ScimDirectoryStatus::Deleted;
            if self
                .compare_and_put("scim_directories", id, &current, &version)
                .await?
            {
                completed = true;
                break;
            }
        }
        if !completed {
            return Err(StoreError::Contention);
        }
        self.release_unique_if_owned("scim-token", &record.token_key, &record.id)
            .await?;
        Ok(())
    }
    find_one!(
        ScimDirectory,
        "scim_directories",
        find_scim_directory_by_token,
        "tokenKey"
    );
    list_all!(
        ScimDirectory,
        "scim_directories",
        list_scim_directories,
        "instanceId",
        100
    );
    record_rw!(
        Subscription,
        "subscriptions",
        get_subscription,
        upsert_subscription
    );
    record_create!(Subscription, "subscriptions", create_subscription);
    find_one!(
        Subscription,
        "subscriptions",
        find_subscription_by_customer,
        "stripeCustomerId"
    );
    list_all!(
        Subscription,
        "subscriptions",
        list_subscriptions,
        "instanceId",
        100
    );
    record_rw!(
        JwtTemplate,
        "jwt_templates",
        get_jwt_template,
        upsert_jwt_template
    );
    record_create!(JwtTemplate, "jwt_templates", create_jwt_template);
    record_delete!("jwt_templates", delete_jwt_template);
    list_all!(
        JwtTemplate,
        "jwt_templates",
        list_jwt_templates,
        "instanceId",
        100
    );
    record_rw!(OrgDomain, "org_domains", get_org_domain, upsert_org_domain);
    pub async fn create_org_domain(&self, record: &OrgDomain) -> StoreResult<OrgDomain> {
        if !self
            .organization_is_active(&record.org_id, &record.instance_id)
            .await?
        {
            return Err(StoreError::Precondition);
        }
        let result = self
            .create_with_unique(
                "org_domains",
                &record.id,
                record,
                "org-domain",
                &record.domain_key,
                &record.id,
            )
            .await;
        if !self
            .organization_is_active(&record.org_id, &record.instance_id)
            .await?
        {
            if result.is_ok() {
                self.delete_org_domain(&record.id).await?;
            }
            return Err(StoreError::Precondition);
        }
        result
    }
    pub async fn delete_org_domain(&self, id: &str) -> StoreResult<()> {
        let record = self.get_org_domain(id).await?;
        self.delete_ignoring_missing("org_domains", id).await?;
        if let Some(record) = record {
            self.release_unique_if_owned("org-domain", &record.domain_key, &record.id)
                .await?;
        }
        Ok(())
    }
    list_all!(
        OrgDomain,
        "org_domains",
        list_org_domains,
        "instanceId",
        100
    );

    pub async fn find_user_by_email(
        &self,
        instance_id: &str,
        normalized_email: &str,
    ) -> StoreResult<Option<User>> {
        let expected_key = format!("{instance_id}:{normalized_email}");
        Ok(self
            .query_all::<User>(
                "users",
                &[Predicate::equal("emailKey", &expected_key)?],
                100,
            )
            .await?
            .into_iter()
            .find(|user| {
                user.instance_id == instance_id
                    && !user.deleted
                    && user.scim_provisioning.is_none()
                    && user.email_key.as_deref() == Some(expected_key.as_str())
            }))
    }
    pub async fn try_update_refresh_token(
        &self,
        id: &str,
        update: impl Fn(&mut RefreshToken) -> bool,
    ) -> StoreResult<bool> {
        for _ in 0..8 {
            let Some((mut token, version)) = self
                .get_versioned::<RefreshToken>("refresh_tokens", id)
                .await?
            else {
                return Ok(false);
            };
            if !update(&mut token) {
                return Ok(false);
            }
            if self
                .compare_and_put("refresh_tokens", id, &token, &version)
                .await?
            {
                return Ok(true);
            }
        }
        Err(StoreError::Contention)
    }
    pub async fn find_oauth_client(
        &self,
        instance_id: &str,
        client_id: &str,
    ) -> StoreResult<Option<OAuthClient>> {
        self.query_one(
            "oauth_clients",
            "clientKey",
            &format!("{instance_id}:{client_id}"),
        )
        .await
    }
    pub async fn list_verification_tokens(
        &self,
        instance_id: &str,
        purpose: &str,
        page_size: u32,
    ) -> StoreResult<Vec<VerificationToken>> {
        self.query_all(
            "verification_tokens",
            &[
                Predicate::equal("instanceId", instance_id)?,
                Predicate::equal("purpose", purpose)?,
            ],
            page_size,
        )
        .await
    }
    pub async fn list_user_verification_tokens(
        &self,
        user_id: &str,
        purpose: &str,
    ) -> StoreResult<Vec<VerificationToken>> {
        self.query_all(
            "verification_tokens",
            &[
                Predicate::equal("userId", user_id)?,
                Predicate::equal("purpose", purpose)?,
            ],
            100,
        )
        .await
    }
    pub async fn list_audit_events(
        &self,
        instance_id: &str,
        limit: usize,
    ) -> StoreResult<Vec<AuditEvent>> {
        let mut events: Vec<AuditEvent> = self
            .query_all(
                "audit_events",
                &[Predicate::equal("instanceId", instance_id)?],
                1000,
            )
            .await?;
        events
            .sort_by(|left, right| (right.created_at, &right.id).cmp(&(left.created_at, &left.id)));
        events.truncate(limit);
        Ok(events)
    }
    pub async fn list_audit_events_since(
        &self,
        instance_id: &str,
        since: i64,
    ) -> StoreResult<Vec<AuditEvent>> {
        let mut events: Vec<AuditEvent> = self
            .query_all(
                "audit_events",
                &[
                    Predicate::equal("instanceId", instance_id)?,
                    Predicate::greater_than_or_equal("createdAt", since)?,
                ],
                1000,
            )
            .await?;
        events
            .sort_by(|left, right| (left.created_at, &left.id).cmp(&(right.created_at, &right.id)));
        Ok(events)
    }
}
