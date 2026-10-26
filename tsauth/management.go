package tsauth

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"path"
	"reflect"
	"strconv"
	"strings"
	"time"
	"unicode"

	"terrascale.tech/sdk/internal/entityid"
)

// Client calls ts-auth's platform management API (contract sections 2-7):
// organizations, members, invitations, ownership transfers, projects,
// project members, API keys and audit events of the terrascale tenant. It is
// safe for concurrent use.
//
//	c, err := tsauth.New(tsauth.Config{})
//	u := c.WithToken(signInToken) // the signed-in user's access token
//	org, err := u.CreateOrganization(ctx, "Acme")
type Client struct {
	base string // {origin}/v1/{tenant}
	hc   *http.Client
}

// Config configures a Client.
type Config struct {
	// Issuer is the tenant issuer; empty means DefaultIssuer.
	Issuer string
	// HTTPClient nil uses a client with a 30 s timeout. It is copied and
	// never follows redirects.
	HTTPClient *http.Client
}

// New returns a management client.
func New(cfg Config) (*Client, error) {
	u, err := parseIssuer(cfg.Issuer)
	if err != nil {
		return nil, err
	}
	dir, tenant := path.Split(u.Path)
	if tenant == "" {
		return nil, fmt.Errorf("tsauth: Issuer %q must name its tenant: https://<host>/<tenant>", cfg.Issuer)
	}
	u.Path = dir + "v1/" + tenant
	return &Client{base: u.String(), hc: noRedirects(cfg.HTTPClient)}, nil
}

// WithToken returns the API as the user of a sign-in access token of the
// tenant. Organization, project, API key and service tokens are refused.
func (c *Client) WithToken(accessToken string) *User {
	return &User{c: c, token: accessToken}
}

// User is the management API as one signed-in user. Organizations the user
// neither belongs to nor collaborates on answer ErrNotFound, like unknown ids.
type User struct {
	c     *Client
	token string
}

// Role is an organization role (viewer, member, admin, owner) or a project
// role (viewer, member, admin).
type Role string

// Roles.
const (
	RoleViewer Role = "viewer"
	RoleMember Role = "member"
	RoleAdmin  Role = "admin"
	RoleOwner  Role = "owner"
)

// Organization is an organization; Role is the caller's role, empty for a
// collaborator on some of its projects.
type Organization struct {
	ID        string    `json:"id" ulid:"required"`
	Name      string    `json:"name"`
	CreatedBy string    `json:"created_by" ulid:"required"`
	CreatedAt time.Time `json:"created_at"`
	Role      Role      `json:"role"`
}

// Member is a member of an organization.
type Member struct {
	UserID   string    `json:"user_id" ulid:"required"`
	Email    string    `json:"email"`
	Role     Role      `json:"role"`
	JoinedAt time.Time `json:"joined_at"`
}

// Project is a project; Issuer is its own tenant, for its end users, and
// Role the caller's effective project role.
type Project struct {
	ID             string    `json:"id" ulid:"required"`
	OrganizationID string    `json:"organization_id" ulid:"required"`
	Name           string    `json:"name"`
	Issuer         string    `json:"issuer"`
	CreatedBy      string    `json:"created_by" ulid:"required"`
	CreatedAt      time.Time `json:"created_at"`
	Role           Role      `json:"role"`
	// AllowedCIDRs restrict every API key token of the project; empty allows any address.
	AllowedCIDRs []string `json:"allowed_cidrs"`
}

// ProjectMember is an explicit role on one project.
type ProjectMember struct {
	UserID    string    `json:"user_id" ulid:"required"`
	Email     string    `json:"email"`
	Role      Role      `json:"role"`
	GrantedAt time.Time `json:"granted_at"`
}

// InvitationStatus is the state of an invitation.
type InvitationStatus string

// Invitation states.
const (
	InvitationPending  InvitationStatus = "pending"
	InvitationAccepted InvitationStatus = "accepted"
	InvitationDeclined InvitationStatus = "declined"
	InvitationRevoked  InvitationStatus = "revoked"
	InvitationExpired  InvitationStatus = "expired"
)

// Invitation invites an email to the organization or, with ProjectID, to
// one project.
type Invitation struct {
	ID        string           `json:"id" ulid:"required"`
	Email     string           `json:"email"`
	Role      Role             `json:"role"`
	ProjectID string           `json:"project_id" ulid:"optional"`
	InvitedBy string           `json:"invited_by" ulid:"required"`
	CreatedAt time.Time        `json:"created_at"`
	ExpiresAt time.Time        `json:"expires_at"`
	Status    InvitationStatus `json:"status"`
}

// CreatedInvitation is a new invitation with its tsi_ token, returned once;
// ts-auth also emails the invitee.
type CreatedInvitation struct {
	Invitation Invitation `json:"invitation"`
	Token      string     `json:"token"`
}

// IDName is an id with its display name.
type IDName struct {
	ID   string `json:"id" ulid:"required"`
	Name string `json:"name"`
}

// InvitationLookup is a pending invitation found by its token.
type InvitationLookup struct {
	Organization IDName    `json:"organization"`
	Project      *IDName   `json:"project"`
	Email        string    `json:"email"`
	Role         Role      `json:"role"`
	ExpiresAt    time.Time `json:"expires_at"`
}

// AcceptedInvitation is the membership or project grant an invitation gave.
type AcceptedInvitation struct {
	OrganizationID string `json:"organization_id" ulid:"required"`
	ProjectID      string `json:"project_id" ulid:"optional"`
	Role           Role   `json:"role"`
}

// OwnershipTransfer is an organization's pending ownership transfer.
type OwnershipTransfer struct {
	FromUserID string    `json:"from_user_id" ulid:"required"`
	ToUserID   string    `json:"to_user_id" ulid:"required"`
	CreatedAt  time.Time `json:"created_at"`
	ExpiresAt  time.Time `json:"expires_at"`
}

// APIKey is an organization key (empty ProjectID) or a project key, without
// its secret.
type APIKey struct {
	ID             string     `json:"id" ulid:"required"`
	Name           string     `json:"name"`
	OrganizationID string     `json:"organization_id" ulid:"required"`
	ProjectID      string     `json:"project_id" ulid:"optional"`
	Database       string     `json:"database"`
	Scopes         []string   `json:"scopes"`
	CreatedBy      string     `json:"created_by" ulid:"required"`
	CreatedAt      time.Time  `json:"created_at"`
	ExpiresAt      *time.Time `json:"expires_at"`
	// LastUsedAt is the last successful token exchange, up to five minutes stale; nil means never used.
	LastUsedAt *time.Time `json:"last_used_at"`
	// AllowedCIDRs are the client networks the key works from; empty allows
	// any address.
	AllowedCIDRs []string `json:"allowed_cidrs"`
	// PreviousSecretExpiresAt is until when the secret before the last
	// rotation still works.
	PreviousSecretExpiresAt *time.Time `json:"previous_secret_expires_at"`
}

// CreatedAPIKey is a key with its whole <ULID>_<52> secret, returned once.
type CreatedAPIKey struct {
	APIKey APIKey `json:"api_key"`
	Key    string `json:"key"`
}

// APIKeyRequest creates an API key.
type APIKeyRequest struct {
	Name string
	// Scopes is a non-empty subset of the Scope constants.
	Scopes []string
	// Database restricts a project key to one database name.
	Database string
	// ExpiresAt, when set, ends the key.
	ExpiresAt *time.Time
	// AllowedCIDRs, when set, limits the key to these client networks, such
	// as 203.0.113.0/24 or 2001:db8::/32.
	AllowedCIDRs []string
}

// AuditEvent is one recorded change, newest first.
type AuditEvent struct {
	ID         string    `json:"id" ulid:"required"`
	Actor      string    `json:"actor" ulid:"required"`
	Action     string    `json:"action"`
	Target     string    `json:"target"`
	Detail     string    `json:"detail"`
	OccurredAt time.Time `json:"occurred_at"`
}

// Page is one page of a list.
type Page[T any] struct {
	Items []T `json:"items"`
	// Next is passed back as PageOptions.After; empty on the last page.
	Next string `json:"next"`
}

// PageOptions pages a list: Limit is 1-100 (zero uses the default of 20).
type PageOptions struct {
	Limit int
	After string
}

func (o PageOptions) query() url.Values {
	q := url.Values{}
	if o.Limit > 0 {
		q.Set("limit", strconv.Itoa(o.Limit))
	}
	if o.After != "" {
		q.Set("after", o.After)
	}
	return q
}

type nameBody struct {
	Name string `json:"name"`
}

type roleBody struct {
	Role Role `json:"role"`
}

type tokenBody struct {
	Token string `json:"token"`
}

func orgPath(org string, extra ...string) []string {
	return append([]string{"organizations", org}, extra...)
}

func projectPath(org, project string, extra ...string) []string {
	return orgPath(org, append([]string{"projects", project}, extra...)...)
}

// keyPath is the path of an organization key, or of a project key.
func keyPath(org, project string, extra ...string) []string {
	if project == "" {
		return orgPath(org, append([]string{"api-keys"}, extra...)...)
	}
	return projectPath(org, project, append([]string{"api-keys"}, extra...)...)
}

// CreateOrganization creates an organization with the user as its owner.
func (u *User) CreateOrganization(ctx context.Context, name string) (*Organization, error) {
	return call[Organization](ctx, u, http.MethodPost, []string{"organizations"}, nil, nameBody{name})
}

// Organizations lists the organizations the user belongs to or collaborates on.
func (u *User) Organizations(ctx context.Context, opts PageOptions) (*Page[Organization], error) {
	return call[Page[Organization]](ctx, u, http.MethodGet, []string{"organizations"}, opts.query(), nil)
}

// Organization reads an organization.
func (u *User) Organization(ctx context.Context, org string) (*Organization, error) {
	return call[Organization](ctx, u, http.MethodGet, orgPath(org), nil, nil)
}

// RenameOrganization renames an organization; it needs admin.
func (u *User) RenameOrganization(ctx context.Context, org, name string) (*Organization, error) {
	return call[Organization](ctx, u, http.MethodPatch, orgPath(org), nil, nameBody{name})
}

// DeleteOrganization deletes an organization without projects; it needs owner.
func (u *User) DeleteOrganization(ctx context.Context, org string) error {
	return u.none(ctx, http.MethodDelete, orgPath(org), nil, nil)
}

// Members lists an organization's members.
func (u *User) Members(ctx context.Context, org string, opts PageOptions) (*Page[Member], error) {
	return call[Page[Member]](ctx, u, http.MethodGet, orgPath(org, "members"), opts.query(), nil)
}

// SetMemberRole changes a member's role; it needs admin, and owner when the
// member is or becomes an owner. The last owner cannot be demoted.
func (u *User) SetMemberRole(ctx context.Context, org, user string, role Role) (*Member, error) {
	return call[Member](ctx, u, http.MethodPut, orgPath(org, "members", user, "role"), nil, roleBody{role})
}

// RemoveMember removes a member; it needs admin, and owner to remove an owner.
func (u *User) RemoveMember(ctx context.Context, org, user string) error {
	return u.none(ctx, http.MethodDelete, orgPath(org, "members", user), nil, nil)
}

// Leave removes the user from an organization; the last owner cannot leave.
func (u *User) Leave(ctx context.Context, org string) error {
	return u.none(ctx, http.MethodPost, orgPath(org, "leave"), nil, nil)
}

// CreateInvitation invites email with an organization role or, with
// project, a project role. ts-auth emails the invitee.
func (u *User) CreateInvitation(ctx context.Context, org, project, email string, role Role) (*CreatedInvitation, error) {
	body := struct {
		Email     string  `json:"email"`
		Role      Role    `json:"role"`
		ProjectID *string `json:"project_id,omitempty" ulid:"optional"`
	}{Email: email, Role: role}
	if project != "" {
		body.ProjectID = &project
	}
	return call[CreatedInvitation](ctx, u, http.MethodPost, orgPath(org, "invitations"), nil, body)
}

// Invitations lists an organization's invitations, or one project's when
// project is not empty.
func (u *User) Invitations(ctx context.Context, org, project string, opts PageOptions) (*Page[Invitation], error) {
	q := opts.query()
	if project != "" {
		q.Set("project_id", project)
	}
	return call[Page[Invitation]](ctx, u, http.MethodGet, orgPath(org, "invitations"), q, nil)
}

// RevokeInvitation revokes a pending invitation.
func (u *User) RevokeInvitation(ctx context.Context, org, invitation string) error {
	return u.none(ctx, http.MethodDelete, orgPath(org, "invitations", invitation), nil, nil)
}

// LookupInvitation reads the pending invitation of a token.
func (u *User) LookupInvitation(ctx context.Context, token string) (*InvitationLookup, error) {
	return call[InvitationLookup](ctx, u, http.MethodPost, []string{"invitations", "lookup"}, nil, tokenBody{token})
}

// AcceptInvitation accepts an invitation; the user's verified email must
// match it (ErrEmailMismatch otherwise).
func (u *User) AcceptInvitation(ctx context.Context, token string) (*AcceptedInvitation, error) {
	return call[AcceptedInvitation](ctx, u, http.MethodPost, []string{"invitations", "accept"}, nil, tokenBody{token})
}

// DeclineInvitation declines an invitation addressed to the user's email.
func (u *User) DeclineInvitation(ctx context.Context, token string) error {
	return u.none(ctx, http.MethodPost, []string{"invitations", "decline"}, nil, tokenBody{token})
}

// OwnershipTransfer reads the organization's pending ownership transfer.
func (u *User) OwnershipTransfer(ctx context.Context, org string) (*OwnershipTransfer, error) {
	return call[OwnershipTransfer](ctx, u, http.MethodGet, orgPath(org, "ownership-transfer"), nil, nil)
}

// StartOwnershipTransfer offers ownership to a member who is not an owner;
// it needs owner.
func (u *User) StartOwnershipTransfer(ctx context.Context, org, user string) (*OwnershipTransfer, error) {
	return call[OwnershipTransfer](ctx, u, http.MethodPost, orgPath(org, "ownership-transfer"), nil, map[string]string{"user_id": user})
}

// CancelOwnershipTransfer cancels the pending transfer, as the owner or the nominee.
func (u *User) CancelOwnershipTransfer(ctx context.Context, org string) error {
	return u.none(ctx, http.MethodDelete, orgPath(org, "ownership-transfer"), nil, nil)
}

// ConfirmOwnershipTransfer accepts the transfer as its nominee, who becomes
// an owner.
func (u *User) ConfirmOwnershipTransfer(ctx context.Context, org string) (*Member, error) {
	return call[Member](ctx, u, http.MethodPost, orgPath(org, "ownership-transfer", "confirm"), nil, nil)
}

// CreateProject creates a project, and its tenant, with the user as its admin.
func (u *User) CreateProject(ctx context.Context, org, name string) (*Project, error) {
	return call[Project](ctx, u, http.MethodPost, orgPath(org, "projects"), nil, nameBody{name})
}

// Projects lists an organization's projects the user can reach.
func (u *User) Projects(ctx context.Context, org string, opts PageOptions) (*Page[Project], error) {
	return call[Page[Project]](ctx, u, http.MethodGet, orgPath(org, "projects"), opts.query(), nil)
}

// Project reads a project.
func (u *User) Project(ctx context.Context, org, project string) (*Project, error) {
	return call[Project](ctx, u, http.MethodGet, projectPath(org, project), nil, nil)
}

// RenameProject renames a project; it needs project admin.
func (u *User) RenameProject(ctx context.Context, org, project, name string) (*Project, error) {
	return call[Project](ctx, u, http.MethodPatch, projectPath(org, project), nil, nameBody{name})
}

// ProjectDeletionResult is the outcome of one project closure request, derived
// from the HTTP status rather than a JSON field. Its zero value is not success.
type ProjectDeletionResult uint8

const (
	// ProjectDeletionPending means 202: closure was accepted but is still pending.
	ProjectDeletionPending ProjectDeletionResult = iota + 1
	// ProjectDeletionCompleted means 204: logical closure completed, not physical erasure.
	ProjectDeletionCompleted
)

// DeleteProject requests closure as project admin. It makes one request without
// polling or retries. Pending closure can be resumed by repeating the request;
// 409 not_empty, closure_conflict or binding_conflict remain *APIError values.
func (u *User) DeleteProject(ctx context.Context, org, project string) (ProjectDeletionResult, error) {
	status, err := u.doStatus(ctx, http.MethodDelete, projectPath(org, project), nil, nil, nil)
	if err != nil {
		return 0, err
	}
	switch status {
	case http.StatusAccepted:
		return ProjectDeletionPending, nil
	case http.StatusNoContent:
		return ProjectDeletionCompleted, nil
	default:
		return 0, fmt.Errorf("tsauth: DELETE project: unexpected success status %d", status)
	}
}

// SetProjectAllowedCIDRs replaces the project's allowlist as project admin.
// Pass an explicit empty slice to allow any address; nil is invalid. The server
// validates up to 32 IPv4/IPv6 networks or addresses. Newly issued key tokens
// adopt the policy within 60 seconds, in addition to their key's own allowlist.
func (u *User) SetProjectAllowedCIDRs(ctx context.Context, org, project string, cidrs []string) (*Project, error) {
	return call[Project](ctx, u, http.MethodPut, projectPath(org, project, "allowed-cidrs"), nil, map[string][]string{"allowed_cidrs": cidrs})
}

// ProjectMembers lists a project's explicit members.
func (u *User) ProjectMembers(ctx context.Context, org, project string, opts PageOptions) (*Page[ProjectMember], error) {
	return call[Page[ProjectMember]](ctx, u, http.MethodGet, projectPath(org, project, "members"), opts.query(), nil)
}

// SetProjectMemberRole grants or changes an organization member's explicit
// project role; it needs project admin.
func (u *User) SetProjectMemberRole(ctx context.Context, org, project, user string, role Role) (*ProjectMember, error) {
	return call[ProjectMember](ctx, u, http.MethodPut, projectPath(org, project, "members", user), nil, roleBody{role})
}

// RemoveProjectMember removes a project member's explicit role.
func (u *User) RemoveProjectMember(ctx context.Context, org, project, user string) error {
	return u.none(ctx, http.MethodDelete, projectPath(org, project, "members", user), nil, nil)
}

// CreateAPIKey creates an organization key (empty project, which needs
// organization admin) or a project key (project member).
func (u *User) CreateAPIKey(ctx context.Context, org, project string, req APIKeyRequest) (*CreatedAPIKey, error) {
	if project == "" && req.Database != "" {
		return nil, errors.New("tsauth: only a project key can name a database")
	}
	body := struct {
		Name         string     `json:"name"`
		Scopes       []string   `json:"scopes"`
		Database     *string    `json:"database,omitempty"`
		ExpiresAt    *time.Time `json:"expires_at,omitempty"`
		AllowedCIDRs []string   `json:"allowed_cidrs,omitempty"`
	}{Name: req.Name, Scopes: req.Scopes, ExpiresAt: req.ExpiresAt, AllowedCIDRs: req.AllowedCIDRs}
	if req.Database != "" {
		body.Database = &req.Database
	}
	return call[CreatedAPIKey](ctx, u, http.MethodPost, keyPath(org, project), nil, body)
}

// APIKeys lists an organization's keys, or a project's.
func (u *User) APIKeys(ctx context.Context, org, project string, opts PageOptions) (*Page[APIKey], error) {
	return call[Page[APIKey]](ctx, u, http.MethodGet, keyPath(org, project), opts.query(), nil)
}

// APIKey reads a key.
func (u *User) APIKey(ctx context.Context, org, project, key string) (*APIKey, error) {
	return call[APIKey](ctx, u, http.MethodGet, keyPath(org, project, key), nil, nil)
}

// SetAPIKeyAllowedCIDRs replaces a key's client network allowlist; an empty
// list allows any address.
func (u *User) SetAPIKeyAllowedCIDRs(ctx context.Context, org, project, key string, cidrs []string) (*APIKey, error) {
	if cidrs == nil {
		cidrs = []string{}
	}
	return call[APIKey](ctx, u, http.MethodPut, keyPath(org, project, key, "allowed-cidrs"), nil, map[string][]string{"allowed_cidrs": cidrs})
}

// RevokeAPIKey revokes a key at once; tokens already issued live out their
// 15 minutes.
func (u *User) RevokeAPIKey(ctx context.Context, org, project, key string) error {
	return u.none(ctx, http.MethodDelete, keyPath(org, project, key), nil, nil)
}

// RotateAPIKey issues a new secret for the same key id; the previous secret
// keeps working for previous (whole seconds, at most 24 hours).
func (u *User) RotateAPIKey(ctx context.Context, org, project, key string, previous time.Duration) (*CreatedAPIKey, error) {
	// Validated before truncating: -500 ms must not become a valid 0.
	if previous < 0 || previous > 24*time.Hour {
		return nil, errors.New("tsauth: the previous secret's grace period must be 0 to 24 hours")
	}
	body := map[string]int64{"previous_secret_ttl": int64(previous / time.Second)}
	return call[CreatedAPIKey](ctx, u, http.MethodPost, keyPath(org, project, key, "rotate"), nil, body)
}

// AuditEvents lists an organization's audit events, newest first; it needs admin.
func (u *User) AuditEvents(ctx context.Context, org string, opts PageOptions) (*Page[AuditEvent], error) {
	return call[Page[AuditEvent]](ctx, u, http.MethodGet, orgPath(org, "audit-events"), opts.query(), nil)
}

// ErrorCode is a management API error code; each is also an error value, so
// errors.Is(err, tsauth.ErrNotFound) matches an *APIError with that code.
type ErrorCode string

func (c ErrorCode) Error() string { return "tsauth: " + string(c) }

// Management API error codes (contract section 4).
const (
	ErrInvalidRequest         ErrorCode = "invalid_request"
	ErrUnauthorized           ErrorCode = "unauthorized"
	ErrForbiddenRole          ErrorCode = "forbidden_role"
	ErrEmailMismatch          ErrorCode = "email_mismatch"
	ErrNotFound               ErrorCode = "not_found" // also an organization the user cannot reach
	ErrNameTaken              ErrorCode = "name_taken"
	ErrNotEmpty               ErrorCode = "not_empty"
	ErrLastOwner              ErrorCode = "last_owner"
	ErrAlreadyMember          ErrorCode = "already_member"
	ErrInvitationPending      ErrorCode = "invitation_pending"
	ErrInvitationExpired      ErrorCode = "invitation_expired"
	ErrNotPending             ErrorCode = "not_pending"
	ErrTemporarilyUnavailable ErrorCode = "temporarily_unavailable"
)

// APIError is a non-2xx response of a JSON API.
type APIError struct {
	Status  int
	Code    ErrorCode
	Message string
	// RetryAfter is the parsed Retry-After of a 429.
	RetryAfter time.Duration
}

func (e *APIError) Error() string {
	return fmt.Sprintf("tsauth: %d %s: %s", e.Status, e.Code, e.Message)
}

// Is reports whether target is the ErrorCode carried by e.
func (e *APIError) Is(target error) bool {
	code, ok := target.(ErrorCode)
	return ok && code == e.Code
}

func call[T any](ctx context.Context, u *User, method string, path []string, query url.Values, in any) (*T, error) {
	var out T
	if err := u.do(ctx, method, path, query, in, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

func (u *User) none(ctx context.Context, method string, path []string, query url.Values, in any) error {
	return u.do(ctx, method, path, query, in, nil)
}

// do sends one request. A path segment may not be empty, a dot segment or
// contain a slash, since it would address another resource.
func (u *User) do(ctx context.Context, method string, path []string, query url.Values, in, out any) error {
	_, err := u.doStatus(ctx, method, path, query, in, out)
	return err
}

func (u *User) doStatus(ctx context.Context, method string, path []string, query url.Values, in, out any) (int, error) {
	if u.token == "" || strings.ContainsFunc(u.token, func(r rune) bool { return unicode.IsSpace(r) || unicode.IsControl(r) }) {
		return 0, errors.New("tsauth: WithToken needs a sign-in access token without whitespace")
	}
	if err := entityid.Path(path); err != nil {
		return 0, fmt.Errorf("tsauth: %s request path: %w", method, err)
	}
	if project := query.Get("project_id"); project != "" {
		if err := entityid.Check(project); err != nil {
			return 0, fmt.Errorf("tsauth: %s request project_id: %w", method, err)
		}
	}
	target := u.c.base
	for _, s := range path {
		if s == "" || s == "." || s == ".." || strings.Contains(s, "/") {
			return 0, fmt.Errorf("tsauth: path segment %q is empty, a dot segment or contains a slash", s)
		}
		target += "/" + url.PathEscape(s)
	}
	if len(query) > 0 {
		target += "?" + query.Encode()
	}
	return sendJSONStatus(ctx, u.c.hc, method, target, "Bearer "+u.token, in, out)
}

// sendJSON sends a JSON request with an Authorization header and decodes a
// 2xx JSON answer into out; other answers are *APIError.
func sendJSON(ctx context.Context, hc *http.Client, method, target, authorization string, in, out any) error {
	_, err := sendJSONStatus(ctx, hc, method, target, authorization, in, out)
	return err
}

func sendJSONStatus(ctx context.Context, hc *http.Client, method, target, authorization string, in, out any) (int, error) {
	if err := ctx.Err(); err != nil {
		return 0, err
	}
	ctx, cancel := context.WithTimeout(ctx, 30*time.Second)
	defer cancel()
	var body io.Reader
	if in != nil {
		data, err := json.Marshal(in)
		if err != nil {
			return 0, fmt.Errorf("tsauth: encode request: %w", err)
		}
		if err := entityid.Body(data); err != nil {
			return 0, fmt.Errorf("tsauth: %s request body: %w", method, err)
		}
		body = bytes.NewReader(data)
	}
	req, err := http.NewRequestWithContext(ctx, method, target, body)
	if err != nil {
		return 0, err
	}
	req.Header.Set("Authorization", authorization)
	req.Header.Set("Accept", "application/json")
	if in != nil {
		req.Header.Set("Content-Type", "application/json")
	}
	if err := ctx.Err(); err != nil {
		return 0, err
	}
	res, err := hc.Do(req)
	if err != nil {
		return 0, fmt.Errorf("tsauth: %s %s: %w", method, req.URL.Path, err)
	}
	defer res.Body.Close()
	data, err := io.ReadAll(io.LimitReader(res.Body, (8<<20)+1))
	if err != nil {
		return 0, fmt.Errorf("tsauth: read %s %s response: %w", method, req.URL.Path, err)
	}
	if len(data) > 8<<20 {
		return 0, &ResponseContractError{}
	}
	if res.StatusCode < 200 || res.StatusCode > 299 {
		e := &APIError{Status: res.StatusCode, Code: ErrorCode("http_" + strconv.Itoa(res.StatusCode)), RetryAfter: retryAfter(res.Header.Get("Retry-After"))}
		var env struct {
			Error struct {
				Code    string `json:"code"`
				Message string `json:"message"`
			} `json:"error"`
		}
		if json.Unmarshal(data, &env) == nil && env.Error.Code != "" {
			e.Code, e.Message = ErrorCode(env.Error.Code), env.Error.Message
		}
		return res.StatusCode, e
	}
	// Decoding errors quote response values, such as a created key: the
	// cause is dropped.
	if out != nil {
		if bytes.Equal(bytes.TrimSpace(data), []byte("null")) || json.Unmarshal(data, out) != nil {
			return 0, &ResponseContractError{}
		}
		if err := validateAuthResponse(data, reflect.TypeOf(out)); err != nil {
			return 0, err
		}
	}
	if err := entityid.Model(out); err != nil {
		return 0, &ResponseContractError{}
	}
	return res.StatusCode, nil
}
