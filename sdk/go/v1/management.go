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

type Management struct{ c *Client }

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

func (m *Management) WithToken(accessToken string) *ManagementUser {
	u := &ManagementUser{c: m.c, token: accessToken}
	if accessToken == "" || strings.ContainsFunc(accessToken, invalidTokenRune) {
		u.err = errors.New("terrabase: WithToken needs a ts-auth access token without whitespace")
	}
	return u
}

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

type ManagementUser struct {
	c      *Client
	token  string
	header http.Header
	err    error
}

type Plan string

const (
	PlanHobby      Plan = "hobby"
	PlanPro        Plan = "pro"
	PlanEnterprise Plan = "enterprise"
)

type Organization struct {
	ID        string    `json:"id" ulid:"required"`
	Name      string    `json:"name"`
	CreatedBy string    `json:"createdBy" ulid:"required"`
	CreatedAt time.Time `json:"createdAt"`
	Plan      Plan      `json:"plan"`
}

type Project struct {
	ID             string    `json:"id" ulid:"required"`
	OrganizationID string    `json:"organizationId" ulid:"required"`
	Name           string    `json:"name"`
	CreatedAt      time.Time `json:"createdAt"`
}

type ManagedDatabase struct {
	_                  struct{}
	ID                 string      `json:"id" ulid:"required"`
	ProjectID          string      `json:"projectId" ulid:"required"`
	Name               string      `json:"name"`
	PreviousName       *string     `json:"previousName,omitempty"`
	ConflictOf         *string     `json:"conflictOf,omitempty" ulid:"optional"`
	BranchedFrom       *string     `json:"branchedFrom" ulid:"optional"`
	CreatedAt          time.Time   `json:"createdAt"`
	ExpiresAt          *time.Time  `json:"expiresAt"`
	Region             string      `json:"region"`
	Regions            []string    `json:"regions"`
	AllRegions         bool        `json:"allRegions"`
	PlacementVersion   int64       `json:"placementVersion"`
	DefaultConsistency Consistency `json:"defaultConsistency,omitempty"`
}

type DatabaseUsage struct {
	DatabaseID   string `json:"databaseId" ulid:"required"`
	StorageBytes int64  `json:"storageBytes"`
	Reads        int64  `json:"reads"`
	Writes       int64  `json:"writes"`
}

type Page[T any] struct {
	Items []T    `json:"items"`
	Next  string `json:"next"`
}

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
	return append([]string{"v1", "projects", project, "databases", databaseID}, extra...)
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

func (u *ManagementUser) CreateOrganization(ctx context.Context, name string) (*Organization, error) {
	return send[Organization](ctx, u, http.MethodPost, managementPath("organizations"), nameBody{name})
}

func (u *ManagementUser) Organizations(ctx context.Context, opts PageOptions) (*Page[Organization], error) {
	return get[Page[Organization]](ctx, u, managementPath("organizations"), opts.query())
}

func (u *ManagementUser) Organization(ctx context.Context, org string) (*Organization, error) {
	return get[Organization](ctx, u, orgPath(org), nil)
}

func (u *ManagementUser) SetPlan(ctx context.Context, org string, plan Plan) (*Organization, error) {
	return send[Organization](ctx, u, http.MethodPut, orgPath(org, "plan"), map[string]Plan{"plan": plan})
}

func (u *ManagementUser) DeleteOrganization(ctx context.Context, org string) error {
	return u.call(ctx, http.MethodDelete, orgPath(org), nil, nil, nil)
}

func (u *ManagementUser) CreateProject(ctx context.Context, org, name string) (*Project, error) {
	return send[Project](ctx, u, http.MethodPost, orgPath(org, "projects"), nameBody{name})
}

func (u *ManagementUser) Projects(ctx context.Context, org string, opts PageOptions) (*Page[Project], error) {
	return get[Page[Project]](ctx, u, orgPath(org, "projects"), opts.query())
}

func (u *ManagementUser) Project(ctx context.Context, org, project string) (*Project, error) {
	return get[Project](ctx, u, projectPath(org, project), nil)
}

func (u *ManagementUser) DeleteProject(ctx context.Context, org, project string) error {
	return u.call(ctx, http.MethodDelete, projectPath(org, project), nil, nil, nil)
}

func (u *ManagementUser) CreateDatabase(ctx context.Context, org, project, name string) (*ManagedDatabase, error) {
	return u.CreateDatabaseIn(ctx, org, project, name, "")
}

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

func (u *ManagementUser) Databases(ctx context.Context, org, project string, opts PageOptions) (*Page[ManagedDatabase], error) {
	return get[Page[ManagedDatabase]](ctx, u, projectAPIPath(project, "databases"), opts.query())
}

func (u *ManagementUser) Database(ctx context.Context, org, project, databaseID string) (*ManagedDatabase, error) {
	return get[ManagedDatabase](ctx, u, databasePath(project, databaseID), nil)
}

func (u *ManagementUser) DeleteDatabase(ctx context.Context, org, project, databaseID string) error {
	return u.call(ctx, http.MethodDelete, databasePath(project, databaseID), nil, nil, nil)
}

func (u *ManagementUser) DatabaseUsage(ctx context.Context, org, project, databaseID string) (*DatabaseUsage, error) {
	return get[DatabaseUsage](ctx, u, databasePath(project, databaseID, "usage"), nil)
}

func (u *ManagementUser) BranchDatabase(ctx context.Context, org, project, databaseID, name string) (*ManagedDatabase, error) {
	return send[ManagedDatabase](ctx, u, http.MethodPost, databasePath(project, databaseID, "branches"), nameBody{name})
}
