use super::*;

/// Project organization.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Organization {
    /// id.
    pub id: String,
    /// name.
    pub name: String,
    /// slug.
    pub slug: String,
    /// image url.
    pub image_url: Option<String>,
    /// public metadata.
    pub public_metadata: serde_json::Map<String, Value>,
    /// private metadata.
    pub private_metadata: serde_json::Map<String, Value>,
    /// max members.
    pub max_members: Option<i32>,
    /// domain join.
    pub domain_join: String,
    /// created at.
    pub created_at: String,
}

/// Project organization request.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct OrganizationRequest {
    /// name.
    pub name: String,
    /// slug.
    pub slug: String,
    /// admin user id.
    pub admin_user_id: Option<String>,
    /// image url.
    pub image_url: Option<String>,
    /// public metadata.
    pub public_metadata: Option<serde_json::Map<String, Value>>,
    /// private metadata.
    pub private_metadata: Option<serde_json::Map<String, Value>>,
    /// max members.
    pub max_members: Option<i32>,
    /// domain join.
    pub domain_join: String,
}

/// Project organization membership.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct OrganizationMembership {
    /// project id.
    pub project_id: String,
    /// organization id.
    pub organization_id: String,
    /// user id.
    pub user_id: String,
    /// role.
    pub role: String,
    /// created at.
    pub created_at: String,
}

/// Project organization membership request.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct OrganizationMembershipRequest {
    /// user id.
    pub user_id: Option<String>,
    /// role.
    pub role: String,
}

/// Project organization invitation.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct OrganizationInvitation {
    /// id.
    pub id: String,
    /// email.
    pub email: String,
    /// role.
    pub role: String,
    /// status.
    pub status: String,
    /// created at.
    pub created_at: String,
    /// expires at.
    pub expires_at: String,
}

/// Project organization invitation request.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct OrganizationInvitationRequest {
    /// Optional plain-text inviter name.
    pub inviter_name: Option<String>,
    /// email.
    pub email: String,
    /// role.
    pub role: String,
}

/// Project organization domain.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct OrganizationDomain {
    /// domain.
    pub domain: String,
    /// record name.
    pub record_name: String,
    /// record value.
    pub record_value: String,
    /// verified at.
    pub verified_at: Option<String>,
}

/// Project organization domain request.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct OrganizationDomainRequest {
    /// domain.
    pub domain: String,
}

/// A page of project organization records.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct OrganizationPage {
    /// Returned records.
    pub items: Vec<Organization>,
    /// Next page cursor.
    pub next: Option<String>,
}

/// A page of project organization_membership records.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct OrganizationMembershipPage {
    /// Returned records.
    pub items: Vec<OrganizationMembership>,
    /// Next page cursor.
    pub next: Option<String>,
}

/// A page of project organization_invitation records.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct OrganizationInvitationPage {
    /// Returned records.
    pub items: Vec<OrganizationInvitation>,
    /// Next page cursor.
    pub next: Option<String>,
}

/// A page of project organization_domain records.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct OrganizationDomainPage {
    /// Returned records.
    pub items: Vec<OrganizationDomain>,
}

impl ProjectsClient {
    /// POST /v1/{project}/organizations.
    pub async fn create_organization(
        &self,
        credential: &str,
        project: &str,
        request: &OrganizationRequest,
    ) -> Result<Organization> {
        let path = format!("/{}/organizations", self.project(project)?);
        let body = serde_json::to_value(request).map_err(invalid_body)?;
        self.send_typed(
            "auth_projects.create_organization",
            Method::Post,
            &path,
            Some(&body),
            credential,
        )
        .await
    }
    /// GET /v1/{project}/organizations.
    pub async fn list_organizations(
        &self,
        credential: &str,
        project: &str,
        limit: Option<u32>,
        after: Option<&str>,
    ) -> Result<OrganizationPage> {
        let path = format!(
            "/{}/organizations{}",
            self.project(project)?,
            self.page_query(None, limit, after)?
        );
        self.send_typed(
            "auth_projects.list_organizations",
            Method::Get,
            &path,
            None,
            credential,
        )
        .await
    }
    /// GET /v1/{project}/organizations/{org}.
    pub async fn get_organization(
        &self,
        credential: &str,
        project: &str,
        org: &str,
    ) -> Result<Organization> {
        let path = format!(
            "/{}/organizations/{}",
            self.project(project)?,
            percent_encode(org)
        );
        self.send_typed(
            "auth_projects.get_organization",
            Method::Get,
            &path,
            None,
            credential,
        )
        .await
    }
    /// PUT /v1/{project}/organizations/{org}.
    pub async fn update_organization(
        &self,
        credential: &str,
        project: &str,
        org: &str,
        request: &OrganizationRequest,
    ) -> Result<Organization> {
        let path = format!(
            "/{}/organizations/{}",
            self.project(project)?,
            percent_encode(org)
        );
        let body = serde_json::to_value(request).map_err(invalid_body)?;
        self.send_typed(
            "auth_projects.update_organization",
            Method::Put,
            &path,
            Some(&body),
            credential,
        )
        .await
    }
    /// DELETE /v1/{project}/organizations/{org}.
    pub async fn delete_organization(
        &self,
        credential: &str,
        project: &str,
        org: &str,
    ) -> Result<()> {
        let path = format!(
            "/{}/organizations/{}",
            self.project(project)?,
            percent_encode(org)
        );
        self.send_no_content(
            "auth_projects.delete_organization",
            Method::Delete,
            &path,
            credential,
        )
        .await
    }
    /// POST /v1/{project}/organizations/{org}/memberships.
    pub async fn add_organization_member(
        &self,
        credential: &str,
        project: &str,
        org: &str,
        request: &OrganizationMembershipRequest,
    ) -> Result<OrganizationMembership> {
        let path = format!(
            "/{}/organizations/{}/memberships",
            self.project(project)?,
            percent_encode(org)
        );
        let body = serde_json::to_value(request).map_err(invalid_body)?;
        self.send_typed(
            "auth_projects.add_organization_member",
            Method::Post,
            &path,
            Some(&body),
            credential,
        )
        .await
    }
    /// GET /v1/{project}/organizations/{org}/memberships.
    pub async fn list_organization_members(
        &self,
        credential: &str,
        project: &str,
        org: &str,
        limit: Option<u32>,
        after: Option<&str>,
    ) -> Result<OrganizationMembershipPage> {
        let path = format!(
            "/{}/organizations/{}/memberships{}",
            self.project(project)?,
            percent_encode(org),
            self.page_query(None, limit, after)?
        );
        self.send_typed(
            "auth_projects.list_organization_members",
            Method::Get,
            &path,
            None,
            credential,
        )
        .await
    }
    /// PUT /v1/{project}/organizations/{org}/memberships/{user}.
    pub async fn update_organization_member(
        &self,
        credential: &str,
        project: &str,
        org: &str,
        user: &str,
        request: &OrganizationMembershipRequest,
    ) -> Result<OrganizationMembership> {
        let path = format!(
            "/{}/organizations/{}/memberships/{}",
            self.project(project)?,
            percent_encode(org),
            percent_encode(user)
        );
        let body = serde_json::to_value(request).map_err(invalid_body)?;
        self.send_typed(
            "auth_projects.update_organization_member",
            Method::Put,
            &path,
            Some(&body),
            credential,
        )
        .await
    }
    /// DELETE /v1/{project}/organizations/{org}/memberships/{user}.
    pub async fn delete_organization_member(
        &self,
        credential: &str,
        project: &str,
        org: &str,
        user: &str,
    ) -> Result<()> {
        let path = format!(
            "/{}/organizations/{}/memberships/{}",
            self.project(project)?,
            percent_encode(org),
            percent_encode(user)
        );
        self.send_no_content(
            "auth_projects.delete_organization_member",
            Method::Delete,
            &path,
            credential,
        )
        .await
    }
    /// POST /v1/{project}/organizations/{org}/invitations.
    pub async fn create_organization_invitation(
        &self,
        credential: &str,
        project: &str,
        org: &str,
        request: &OrganizationInvitationRequest,
    ) -> Result<OrganizationInvitation> {
        let path = format!(
            "/{}/organizations/{}/invitations",
            self.project(project)?,
            percent_encode(org)
        );
        let body = serde_json::to_value(request).map_err(invalid_body)?;
        self.send_typed(
            "auth_projects.create_organization_invitation",
            Method::Post,
            &path,
            Some(&body),
            credential,
        )
        .await
    }
    /// GET /v1/{project}/organizations/{org}/invitations.
    pub async fn list_organization_invitations(
        &self,
        credential: &str,
        project: &str,
        org: &str,
        limit: Option<u32>,
        after: Option<&str>,
    ) -> Result<OrganizationInvitationPage> {
        let path = format!(
            "/{}/organizations/{}/invitations{}",
            self.project(project)?,
            percent_encode(org),
            self.page_query(None, limit, after)?
        );
        self.send_typed(
            "auth_projects.list_organization_invitations",
            Method::Get,
            &path,
            None,
            credential,
        )
        .await
    }
    /// DELETE /v1/{project}/organizations/{org}/invitations/{invitation}.
    pub async fn revoke_organization_invitation(
        &self,
        credential: &str,
        project: &str,
        org: &str,
        invitation: &str,
    ) -> Result<()> {
        let path = format!(
            "/{}/organizations/{}/invitations/{}",
            self.project(project)?,
            percent_encode(org),
            percent_encode(invitation)
        );
        self.send_no_content(
            "auth_projects.revoke_organization_invitation",
            Method::Delete,
            &path,
            credential,
        )
        .await
    }
    /// POST /v1/{project}/organizations/{org}/domains.
    pub async fn add_organization_domain(
        &self,
        credential: &str,
        project: &str,
        org: &str,
        request: &OrganizationDomainRequest,
    ) -> Result<OrganizationDomain> {
        let path = format!(
            "/{}/organizations/{}/domains",
            self.project(project)?,
            percent_encode(org)
        );
        let body = serde_json::to_value(request).map_err(invalid_body)?;
        self.send_typed(
            "auth_projects.add_organization_domain",
            Method::Post,
            &path,
            Some(&body),
            credential,
        )
        .await
    }
    /// GET /v1/{project}/organizations/{org}/domains.
    pub async fn list_organization_domains(
        &self,
        credential: &str,
        project: &str,
        org: &str,
    ) -> Result<OrganizationDomainPage> {
        let path = format!(
            "/{}/organizations/{}/domains",
            self.project(project)?,
            percent_encode(org)
        );
        self.send_typed(
            "auth_projects.list_organization_domains",
            Method::Get,
            &path,
            None,
            credential,
        )
        .await
    }
    /// POST /v1/{project}/organizations/{org}/domains/{domain}/verify.
    pub async fn verify_organization_domain(
        &self,
        credential: &str,
        project: &str,
        org: &str,
        domain: &str,
    ) -> Result<OrganizationDomain> {
        let path = format!(
            "/{}/organizations/{}/domains/{}/verify",
            self.project(project)?,
            percent_encode(org),
            percent_encode(domain)
        );
        self.send_typed(
            "auth_projects.verify_organization_domain",
            Method::Post,
            &path,
            None,
            credential,
        )
        .await
    }
    /// DELETE /v1/{project}/organizations/{org}/domains/{domain}.
    pub async fn delete_organization_domain(
        &self,
        credential: &str,
        project: &str,
        org: &str,
        domain: &str,
    ) -> Result<()> {
        let path = format!(
            "/{}/organizations/{}/domains/{}",
            self.project(project)?,
            percent_encode(org),
            percent_encode(domain)
        );
        self.send_no_content(
            "auth_projects.delete_organization_domain",
            Method::Delete,
            &path,
            credential,
        )
        .await
    }
}
