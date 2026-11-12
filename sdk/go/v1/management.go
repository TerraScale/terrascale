package v1

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"
	"unicode"

	"terrascale.tech/sdk/internal/entityid"
)

// Management is a client of the TerraBase Management API (/v1/management).
// Every call acts as one ts-auth user: WithToken presents the user's ts-auth
// access token on the client listener, and As names the user with the
// peer credential on the internal listener. It is safe for concurrent use.
//
//	m, err := v1.NewManagement(v1.Config{BaseURL: origin})
//	u := m.WithToken(session.AccessToken)
//	org, err := u.CreateOrganization(ctx, "Acme")
type Management struct{ c *Client }

// NewManagement returns a Management API client. Config.Token is empty, or
// the internal peer credential for As: exactly 64 hexadecimal characters.
func NewManagement(cfg Config) (*Management, error) {
	if cfg.Token != "" && (len(cfg.Token) != 64 || strings.Trim(cfg.Token, "0123456789abcdefABCDEF") != "") {
		return nil, errors.New("terrabase: Token must be empty or the internal peer credential: exactly 64 hexadecimal characters")
	}
	c, err := newClient(cfg)
	if err != nil {
		return nil, err
	}
	return &Management{c: c}, nil
}

func invalidTokenRune(r rune) bool { return unicode.IsSpace(r) || unicode.IsControl(r) }

// WithToken returns a view that acts as the user of a ts-auth access token.
// TerraBase accepts it on the client listener only.
func (m *Management) WithToken(accessToken string) *ManagementUser {
	u := &ManagementUser{c: m.c, token: accessToken}
	if accessToken == "" || strings.ContainsFunc(accessToken, invalidTokenRune) {
		u.err = errors.New("terrabase: WithToken needs a ts-auth access token without whitespace")
	}
	return u
}

// As returns a view that acts as the ts-auth user userID with the internal
// peer credential (Config.Token), sending X-TerraBase-Peer-Credential and
// X-TerraBase-User-Id without Authorization on every request. TerraBase accepts
// the credential only on its internal listener. The authenticated caller may
// name the user: never take it from browser input.
func (m *Management) As(userID string) *ManagementUser {
	u := &ManagementUser{c: m.c, header: http.Header{"X-Terrabase-User-Id": {userID}, "X-Terrabase-Peer-Credential": {m.c.token}}}
	if err := checkID(userID); err != nil {
		u.err = fmt.Errorf("terrabase: user ID: %w", err)
	}
	if m.c.token == "" {
		u.err = errors.Join(u.err, errors.New("terrabase: As needs Config.Token, the internal peer credential"))
	}
	return u
}

// ManagementUser is the Management API as seen by one user. Organizations the
// user is not a member of answer 404 (ErrNotFound), like unknown ids.
type ManagementUser struct {
	c      *Client
	token  string      // WithToken
	header http.Header // As
	err    error
}

// Plan is an organization's plan.
type Plan string

// Plans.
const (
	PlanHobby      Plan = "hobby"
	PlanPro        Plan = "pro"
	PlanEnterprise Plan = "enterprise"
)

// Organization is a TerraBase organization, the tenant that owns projects.
type Organization struct {
	ID        string    `json:"id" ulid:"required"`
	Name      string    `json:"name"`
	CreatedBy string    `json:"createdBy" ulid:"required"`
	CreatedAt time.Time `json:"createdAt"`
	Plan      Plan      `json:"plan"`
}

// Project is a folder of databases inside an organization.
type Project struct {
	ID             string    `json:"id" ulid:"required"`
	OrganizationID string    `json:"organizationId" ulid:"required"`
	Name           string    `json:"name"`
	CreatedAt      time.Time `json:"createdAt"`
}

// ManagedDatabase represents lifecycle metadata and catalog restore results.
// Name addresses Client.Database(ProjectID, Name). Restore results omit Region
// and may have null placement; lifecycle results include the serving region.
type ManagedDatabase struct {
	_         struct{}
	ID        string `json:"id" ulid:"required"`
	ProjectID string `json:"projectId" ulid:"required"`
	Name      string `json:"name"`
	// PreviousName is the contested name after a cross-region name conflict.
	PreviousName *string `json:"previousName,omitempty"`
	// ConflictOf is the winning database ULID; a successful rename clears both fields.
	ConflictOf *string `json:"conflictOf,omitempty" ulid:"optional"`
	// BranchedFrom is the id of the database this one was branched from.
	BranchedFrom *string    `json:"branchedFrom" ulid:"optional"`
	CreatedAt    time.Time  `json:"createdAt"`
	ExpiresAt    *time.Time `json:"expiresAt"`
	// Region identifies the answering node; it is absent from restore results.
	Region           string   `json:"region"`
	Regions          []string `json:"regions"`
	AllRegions       bool     `json:"allRegions"`
	PlacementVersion int64    `json:"placementVersion"`
	// Deprecated: raw earlier field, passed through for ts -json; absent once the server stops sending it.
	HomeRegion json.RawMessage `json:"homeRegion,omitempty"`
	// Deprecated: raw earlier field, passed through for ts -json; absent once the server stops sending it.
	ReplicaRegions json.RawMessage `json:"replicaRegions,omitempty"`
}

// DatabaseUsage is a database's storage and its metered reads and writes.
type DatabaseUsage struct {
	DatabaseID   string `json:"databaseId" ulid:"required"`
	StorageBytes int64  `json:"storageBytes"`
	Reads        int64  `json:"reads"`
	Writes       int64  `json:"writes"`
}

// Page is one page of a Management list.
type Page[T any] struct {
	Items []T `json:"items"`
	// Next is passed back as PageOptions.After for the next page; empty on
	// the last page.
	Next string `json:"next"`
}

// PageOptions pages a Management list: After is the previous page's Next and
// Limit is 1-100 (zero uses the server default of 20).
type PageOptions struct {
	After string
	Limit int
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

// call sends one Management request as the user.
func (u *ManagementUser) call(ctx context.Context, method string, path []string, query url.Values, in, out any) error {
	if u.err != nil {
		return u.err
	}
	if err := entityid.Path(path); err != nil {
		return fmt.Errorf("terrabase: %s management request path: %w", method, err)
	}
	var body []byte
	if in != nil {
		var err error
		body, err = json.Marshal(in)
		if err != nil {
			return fmt.Errorf("terrabase: encode %s management request: %w", method, err)
		}
		if err := entityid.Body(body); err != nil {
			return fmt.Errorf("terrabase: %s management request body: %w", method, err)
		}
		if err := entityid.Model(in); err != nil {
			return fmt.Errorf("terrabase: %s management request model: %w", method, err)
		}
	}
	req := request{
		method:     method,
		body:       body,
		path:       path,
		query:      query,
		header:     u.header,
		token:      u.token,
		replaySafe: method == http.MethodGet,
	}
	_, err := u.c.call(ctx, req, nil, out)
	return err
}

func managementPath(parts ...string) []string {
	return append([]string{"v1", "management"}, parts...)
}

func orgPath(org string, extra ...string) []string {
	return managementPath(append([]string{"organizations", org}, extra...)...)
}

func projectPath(org, project string, extra ...string) []string {
	return orgPath(org, append([]string{"projects", project}, extra...)...)
}

func projectAPIPath(project string, extra ...string) []string {
	return append([]string{"v1", "projects", project}, extra...)
}

func databasePath(project, databaseID string, extra ...string) []string {
	return projectAPIPath(project, append([]string{"databases", databaseID}, extra...)...)
}

func get[T any](ctx context.Context, u *ManagementUser, path []string, query url.Values) (*T, error) {
	var out T
	if err := u.call(ctx, http.MethodGet, path, query, nil, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

func send[T any](ctx context.Context, u *ManagementUser, method string, path []string, in any) (*T, error) {
	var out T
	if err := u.call(ctx, method, path, nil, in, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

type nameBody struct {
	Name string `json:"name"`
}

// CreateOrganization creates an organization with the user as its owner.
// A name the user already used fails with ErrConflict.
func (u *ManagementUser) CreateOrganization(ctx context.Context, name string) (*Organization, error) {
	return send[Organization](ctx, u, http.MethodPost, managementPath("organizations"), nameBody{name})
}

// Organizations lists the user's organizations.
func (u *ManagementUser) Organizations(ctx context.Context, opts PageOptions) (*Page[Organization], error) {
	return get[Page[Organization]](ctx, u, managementPath("organizations"), opts.query())
}

// Organization reads an organization.
func (u *ManagementUser) Organization(ctx context.Context, org string) (*Organization, error) {
	return get[Organization](ctx, u, orgPath(org), nil)
}

// SetPlan sets an organization's plan; it needs the owner role.
func (u *ManagementUser) SetPlan(ctx context.Context, org string, plan Plan) (*Organization, error) {
	return send[Organization](ctx, u, http.MethodPut, orgPath(org, "plan"), map[string]Plan{"plan": plan})
}

// DeleteOrganization deletes an organization without projects; it needs the
// owner role.
func (u *ManagementUser) DeleteOrganization(ctx context.Context, org string) error {
	return u.call(ctx, http.MethodDelete, orgPath(org), nil, nil, nil)
}

// CreateProject creates a project in an organization.
func (u *ManagementUser) CreateProject(ctx context.Context, org, name string) (*Project, error) {
	return send[Project](ctx, u, http.MethodPost, orgPath(org, "projects"), nameBody{name})
}

// Projects lists an organization's projects.
func (u *ManagementUser) Projects(ctx context.Context, org string, opts PageOptions) (*Page[Project], error) {
	return get[Page[Project]](ctx, u, orgPath(org, "projects"), opts.query())
}

// Project reads a project.
func (u *ManagementUser) Project(ctx context.Context, org, project string) (*Project, error) {
	return get[Project](ctx, u, projectPath(org, project), nil)
}

// DeleteProject deletes a project without databases.
func (u *ManagementUser) DeleteProject(ctx context.Context, org, project string) error {
	return u.call(ctx, http.MethodDelete, projectPath(org, project), nil, nil, nil)
}

// CreateDatabase creates an empty database in a project, served from the
// called region.
func (u *ManagementUser) CreateDatabase(ctx context.Context, org, project, name string) (*ManagedDatabase, error) {
	return u.CreateDatabaseIn(ctx, org, project, name, "")
}

// CreateDatabaseIn creates an empty database in a project served from region,
// a region id such as gru-1; empty means the called region.
func (u *ManagementUser) CreateDatabaseIn(ctx context.Context, org, project, name, region string) (*ManagedDatabase, error) {
	body := struct {
		Name    string   `json:"name"`
		Regions []string `json:"regions,omitempty"`
	}{name, nil}
	if region != "" {
		body.Regions = []string{region}
	}
	return send[ManagedDatabase](ctx, u, http.MethodPost, projectAPIPath(project, "databases"), body)
}

// Databases lists a project's databases.
func (u *ManagementUser) Databases(ctx context.Context, org, project string, opts PageOptions) (*Page[ManagedDatabase], error) {
	return get[Page[ManagedDatabase]](ctx, u, projectAPIPath(project, "databases"), opts.query())
}

// Database reads a database by id.
func (u *ManagementUser) Database(ctx context.Context, org, project, databaseID string) (*ManagedDatabase, error) {
	return get[ManagedDatabase](ctx, u, databasePath(project, databaseID), nil)
}

// DeleteDatabase deletes a database, by id, and its data; its branches stay.
func (u *ManagementUser) DeleteDatabase(ctx context.Context, org, project, databaseID string) error {
	return u.call(ctx, http.MethodDelete, databasePath(project, databaseID), nil, nil, nil)
}

// DatabaseUsage reads a database's storage, reads and writes.
func (u *ManagementUser) DatabaseUsage(ctx context.Context, org, project, databaseID string) (*DatabaseUsage, error) {
	return get[DatabaseUsage](ctx, u, databasePath(project, databaseID, "usage"), nil)
}

// BranchDatabase creates the database name in the same project, holding a
// consistent snapshot copy of the database databaseID.
func (u *ManagementUser) BranchDatabase(ctx context.Context, org, project, databaseID, name string) (*ManagedDatabase, error) {
	return send[ManagedDatabase](ctx, u, http.MethodPost, databasePath(project, databaseID, "branches"), nameBody{name})
}
