package tsauth

import (
	"context"
	"net/http"
	"net/url"
	"strconv"
	"time"
)

// ProjectOrganization is a project organization.
type ProjectOrganization struct {
	Id              string         `json:"id"`
	Name            string         `json:"name"`
	Slug            string         `json:"slug"`
	ImageUrl        *string        `json:"image_url"`
	PublicMetadata  map[string]any `json:"public_metadata"`
	PrivateMetadata map[string]any `json:"private_metadata"`
	MaxMembers      *int           `json:"max_members"`
	DomainJoin      string         `json:"domain_join"`
	CreatedAt       time.Time      `json:"created_at"`
}

// ProjectOrganizationRequest is a project organization request.
type ProjectOrganizationRequest struct {
	Name            string         `json:"name"`
	Slug            string         `json:"slug"`
	AdminUserId     *string        `json:"admin_user_id"`
	ImageUrl        *string        `json:"image_url"`
	PublicMetadata  map[string]any `json:"public_metadata"`
	PrivateMetadata map[string]any `json:"private_metadata"`
	MaxMembers      *int           `json:"max_members"`
	DomainJoin      string         `json:"domain_join"`
}

// ProjectOrganizationMembership is a project organization membership.
type ProjectOrganizationMembership struct {
	ProjectId             string    `json:"project_id"`
	ProjectOrganizationId string    `json:"organization_id"`
	UserId                string    `json:"user_id"`
	Role                  string    `json:"role"`
	CreatedAt             time.Time `json:"created_at"`
}

// ProjectOrganizationMembershipRequest is a project organization membership request.
type ProjectOrganizationMembershipRequest struct {
	UserId *string `json:"user_id"`
	Role   string  `json:"role"`
}

// ProjectOrganizationInvitation is a project organization invitation.
type ProjectOrganizationInvitation struct {
	Id        string    `json:"id"`
	Email     string    `json:"email"`
	Role      string    `json:"role"`
	Status    string    `json:"status"`
	CreatedAt time.Time `json:"created_at"`
	ExpiresAt time.Time `json:"expires_at"`
}

// ProjectOrganizationInvitationRequest is a project organization invitation request.
type ProjectOrganizationInvitationRequest struct {
	InviterName *string `json:"inviter_name,omitempty"`
	Email       string  `json:"email"`
	Role        string  `json:"role"`
}

// ProjectOrganizationDomain is a project organization domain.
type ProjectOrganizationDomain struct {
	Domain      string     `json:"domain"`
	RecordName  string     `json:"record_name"`
	RecordValue string     `json:"record_value"`
	VerifiedAt  *time.Time `json:"verified_at"`
}

// ProjectOrganizationDomainRequest is a project organization domain request.
type ProjectOrganizationDomainRequest struct {
	Domain string `json:"domain"`
}

// ProjectOrganizationPage holds project organization records.
type ProjectOrganizationPage struct {
	Items []ProjectOrganization `json:"items"`
	Next  *string               `json:"next"`
}

// ProjectOrganizationMembershipPage holds project organization_membership records.
type ProjectOrganizationMembershipPage struct {
	Items []ProjectOrganizationMembership `json:"items"`
	Next  *string                         `json:"next"`
}

// ProjectOrganizationInvitationPage holds project organization_invitation records.
type ProjectOrganizationInvitationPage struct {
	Items []ProjectOrganizationInvitation `json:"items"`
	Next  *string                         `json:"next"`
}

// ProjectOrganizationDomainPage holds project organization_domain records.
type ProjectOrganizationDomainPage struct {
	Items []ProjectOrganizationDomain `json:"items"`
}

// CreateProjectOrganization calls POST /v1/{project}/organizations.
func (u *User) CreateProjectOrganization(ctx context.Context, project string, request ProjectOrganizationRequest) (*ProjectOrganization, error) {
	scoped, err := u.projectClientUser(project)
	if err != nil {
		return nil, err
	}
	return call[ProjectOrganization](ctx, scoped, http.MethodPost, []string{"organizations"}, nil, request)
}

// ListProjectOrganizations calls GET /v1/{project}/organizations.
func (u *User) ListProjectOrganizations(ctx context.Context, project string, limit int, after string) (*ProjectOrganizationPage, error) {
	scoped, err := u.projectClientUser(project)
	if err != nil {
		return nil, err
	}
	query := url.Values{}
	if limit != 0 {
		query.Set("limit", strconv.Itoa(limit))
	}
	if after != "" {
		query.Set("after", after)
	}
	return call[ProjectOrganizationPage](ctx, scoped, http.MethodGet, []string{"organizations"}, query, nil)
}

// GetProjectOrganization calls GET /v1/{project}/organizations/{org}.
func (u *User) GetProjectOrganization(ctx context.Context, project string, org string) (*ProjectOrganization, error) {
	scoped, err := u.projectClientUser(project)
	if err != nil {
		return nil, err
	}
	return call[ProjectOrganization](ctx, scoped, http.MethodGet, []string{"organizations", org}, nil, nil)
}

// UpdateProjectOrganization calls PUT /v1/{project}/organizations/{org}.
func (u *User) UpdateProjectOrganization(ctx context.Context, project string, org string, request ProjectOrganizationRequest) (*ProjectOrganization, error) {
	scoped, err := u.projectClientUser(project)
	if err != nil {
		return nil, err
	}
	return call[ProjectOrganization](ctx, scoped, http.MethodPut, []string{"organizations", org}, nil, request)
}

// DeleteProjectOrganization calls DELETE /v1/{project}/organizations/{org}.
func (u *User) DeleteProjectOrganization(ctx context.Context, project string, org string) error {
	scoped, err := u.projectClientUser(project)
	if err != nil {
		return err
	}
	_, err = call[struct{}](ctx, scoped, http.MethodDelete, []string{"organizations", org}, nil, nil)
	return err
}

// AddProjectOrganizationMember calls POST /v1/{project}/organizations/{org}/memberships.
func (u *User) AddProjectOrganizationMember(ctx context.Context, project string, org string, request ProjectOrganizationMembershipRequest) (*ProjectOrganizationMembership, error) {
	scoped, err := u.projectClientUser(project)
	if err != nil {
		return nil, err
	}
	return call[ProjectOrganizationMembership](ctx, scoped, http.MethodPost, []string{"organizations", org, "memberships"}, nil, request)
}

// ListProjectOrganizationMembers calls GET /v1/{project}/organizations/{org}/memberships.
func (u *User) ListProjectOrganizationMembers(ctx context.Context, project string, org string, limit int, after string) (*ProjectOrganizationMembershipPage, error) {
	scoped, err := u.projectClientUser(project)
	if err != nil {
		return nil, err
	}
	query := url.Values{}
	if limit != 0 {
		query.Set("limit", strconv.Itoa(limit))
	}
	if after != "" {
		query.Set("after", after)
	}
	return call[ProjectOrganizationMembershipPage](ctx, scoped, http.MethodGet, []string{"organizations", org, "memberships"}, query, nil)
}

// UpdateProjectOrganizationMember calls PUT /v1/{project}/organizations/{org}/memberships/{user}.
func (u *User) UpdateProjectOrganizationMember(ctx context.Context, project string, org string, user string, request ProjectOrganizationMembershipRequest) (*ProjectOrganizationMembership, error) {
	scoped, err := u.projectClientUser(project)
	if err != nil {
		return nil, err
	}
	return call[ProjectOrganizationMembership](ctx, scoped, http.MethodPut, []string{"organizations", org, "memberships", user}, nil, request)
}

// DeleteProjectOrganizationMember calls DELETE /v1/{project}/organizations/{org}/memberships/{user}.
func (u *User) DeleteProjectOrganizationMember(ctx context.Context, project string, org string, user string) error {
	scoped, err := u.projectClientUser(project)
	if err != nil {
		return err
	}
	_, err = call[struct{}](ctx, scoped, http.MethodDelete, []string{"organizations", org, "memberships", user}, nil, nil)
	return err
}

// CreateProjectOrganizationInvitation calls POST /v1/{project}/organizations/{org}/invitations.
func (u *User) CreateProjectOrganizationInvitation(ctx context.Context, project string, org string, request ProjectOrganizationInvitationRequest) (*ProjectOrganizationInvitation, error) {
	scoped, err := u.projectClientUser(project)
	if err != nil {
		return nil, err
	}
	return call[ProjectOrganizationInvitation](ctx, scoped, http.MethodPost, []string{"organizations", org, "invitations"}, nil, request)
}

// ListProjectOrganizationInvitations calls GET /v1/{project}/organizations/{org}/invitations.
func (u *User) ListProjectOrganizationInvitations(ctx context.Context, project string, org string, limit int, after string) (*ProjectOrganizationInvitationPage, error) {
	scoped, err := u.projectClientUser(project)
	if err != nil {
		return nil, err
	}
	query := url.Values{}
	if limit != 0 {
		query.Set("limit", strconv.Itoa(limit))
	}
	if after != "" {
		query.Set("after", after)
	}
	return call[ProjectOrganizationInvitationPage](ctx, scoped, http.MethodGet, []string{"organizations", org, "invitations"}, query, nil)
}

// RevokeProjectOrganizationInvitation calls DELETE /v1/{project}/organizations/{org}/invitations/{invitation}.
func (u *User) RevokeProjectOrganizationInvitation(ctx context.Context, project string, org string, invitation string) error {
	scoped, err := u.projectClientUser(project)
	if err != nil {
		return err
	}
	_, err = call[struct{}](ctx, scoped, http.MethodDelete, []string{"organizations", org, "invitations", invitation}, nil, nil)
	return err
}

// AddProjectOrganizationDomain calls POST /v1/{project}/organizations/{org}/domains.
func (u *User) AddProjectOrganizationDomain(ctx context.Context, project string, org string, request ProjectOrganizationDomainRequest) (*ProjectOrganizationDomain, error) {
	scoped, err := u.projectClientUser(project)
	if err != nil {
		return nil, err
	}
	return call[ProjectOrganizationDomain](ctx, scoped, http.MethodPost, []string{"organizations", org, "domains"}, nil, request)
}

// ListProjectOrganizationDomains calls GET /v1/{project}/organizations/{org}/domains.
func (u *User) ListProjectOrganizationDomains(ctx context.Context, project string, org string) (*ProjectOrganizationDomainPage, error) {
	scoped, err := u.projectClientUser(project)
	if err != nil {
		return nil, err
	}
	return call[ProjectOrganizationDomainPage](ctx, scoped, http.MethodGet, []string{"organizations", org, "domains"}, nil, nil)
}

// VerifyProjectOrganizationDomain calls POST /v1/{project}/organizations/{org}/domains/{domain}/verify.
func (u *User) VerifyProjectOrganizationDomain(ctx context.Context, project string, org string, domain string) (*ProjectOrganizationDomain, error) {
	scoped, err := u.projectClientUser(project)
	if err != nil {
		return nil, err
	}
	return call[ProjectOrganizationDomain](ctx, scoped, http.MethodPost, []string{"organizations", org, "domains", domain, "verify"}, nil, nil)
}

// DeleteProjectOrganizationDomain calls DELETE /v1/{project}/organizations/{org}/domains/{domain}.
func (u *User) DeleteProjectOrganizationDomain(ctx context.Context, project string, org string, domain string) error {
	scoped, err := u.projectClientUser(project)
	if err != nil {
		return err
	}
	_, err = call[struct{}](ctx, scoped, http.MethodDelete, []string{"organizations", org, "domains", domain}, nil, nil)
	return err
}
