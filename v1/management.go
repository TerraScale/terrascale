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

// Role is a member's role in an organization, from least to most privileged:
// viewer, member, admin, owner. A viewer only reads.
type Role string

// Roles.
const (
	RoleViewer Role = "viewer"
	RoleMember Role = "member"
	RoleAdmin  Role = "admin"
	RoleOwner  Role = "owner"
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
	ID        string `json:"id" ulid:"required"`
	ProjectID string `json:"projectId" ulid:"required"`
	Name      string `json:"name"`
	// BranchedFrom is the id of the database this one was branched from.
	BranchedFrom *string   `json:"branchedFrom" ulid:"optional"`
	CreatedAt    time.Time `json:"createdAt"`
	// Region identifies the answering node; it is absent from restore results.
	Region string `json:"region"`
	// HomeRegion is null when catalog placement has not been assigned.
	HomeRegion *string `json:"homeRegion"`
	// ReplicaRegions are the region ids that replicate the database.
	ReplicaRegions []string `json:"replicaRegions"`
	// AllRegions means every serving region holds a full copy, including
	// regions that join later; ReplicaRegions is then empty.
	AllRegions bool `json:"allRegions"`
	// PlacementVersion and Replicas are included in catalog restore results.
	PlacementVersion int64    `json:"placementVersion"`
	Replicas         []string `json:"replicas"`
}

// Placement places a new database. Regions are region ids such as
// gru-1, not RegionCode values. An empty HomeRegion is the called region,
// where the database is managed, not a request for single-region placement.
// A writable database requires at least two regions. Omitted ReplicaRegions
// uses every other current serving region; an explicit list must include at
// least one region other than the home. An explicitly empty list on create
// returns 400 placement_needs_two_regions.
// AllRegions places a full copy in every serving region, including regions
// that join later; it cannot be combined with ReplicaRegions.
type Placement struct {
	HomeRegion     string   `json:"homeRegion,omitempty"`
	ReplicaRegions []string `json:"replicaRegions,omitempty"`
	AllRegions     bool     `json:"allRegions,omitempty"`
}

// CollectionStats is a collection with its live documents (tombstones are
// not counted) and their total JSON bytes.
type CollectionStats struct {
	Name      string `json:"name"`
	Documents int64  `json:"documents"`
	Bytes     int64  `json:"bytes"`
}

// DatabaseUsage is a database's storage and its metered reads and writes.
type DatabaseUsage struct {
	DatabaseID   string `json:"databaseId" ulid:"required"`
	StorageBytes int64  `json:"storageBytes"`
	Collections  int64  `json:"collections"`
	Documents    int64  `json:"documents"`
	Reads        int64  `json:"reads"`
	Writes       int64  `json:"writes"`
}

// SQLResult is the rows a dashboard SQL statement selected.
type SQLResult struct {
	Matched int `json:"matched"`
	// Truncated reports that the statement's LIMIT or the 8 MiB bound cut
	// the rows short.
	Truncated bool     `json:"truncated"`
	Rows      []SQLRow `json:"rows"`
}

// SQLRow is one selected document: all of it for SELECT *, otherwise one
// member per selected field.
type SQLRow struct {
	ID    string          `json:"id" documentkey:"required"`
	Value json.RawMessage `json:"value"`
}

// UnmarshalJSON requires the server's object-valued SQL row envelope.
func (r *SQLRow) UnmarshalJSON(data []byte) error {
	if err := requiredResponseMembers(data, "id", "value"); err != nil {
		return err
	}
	type wire SQLRow
	var decoded wire
	if err := json.Unmarshal(data, &decoded); err != nil {
		return err
	}
	if err := checkResponseObject(decoded.Value); err != nil {
		return err
	}
	*r = SQLRow(decoded)
	return nil
}

// IndexKind identifies a secondary index's search strategy.
type IndexKind string

const (
	IndexBTree  IndexKind = "btree"
	IndexText   IndexKind = "text"
	IndexVector IndexKind = "vector"
)

// Index is a secondary index of a database's collection.
type Index struct {
	Name       string `json:"name"`
	Collection string `json:"collection"`
	// Fields are the key fields in order, as dotted member paths.
	Fields []string `json:"fields"`
	// State is active, or building while a vector index adds existing documents.
	State string    `json:"state"`
	Kind  IndexKind `json:"kind,omitempty"`
	// Dimensions is the vector length; absent for other index kinds.
	Dimensions *int `json:"dimensions,omitempty"`
}

// IndexRequest describes an index to create: Fields are 1-4 distinct dotted
// member paths. An empty Name is derived from the collection and fields.
type IndexRequest struct {
	Collection string   `json:"collection"`
	Fields     []string `json:"fields"`
	Name       string   `json:"name,omitempty"`
	// Kind defaults to IndexBTree when omitted.
	Kind IndexKind `json:"kind,omitempty"`
	// Dimensions is required for IndexVector (1-4096, exactly one field); absent for other kinds.
	Dimensions *int `json:"dimensions,omitempty"`
}

// ProjectAPIKey is a project's data API key (tb_…) without its secret.
type ProjectAPIKey struct {
	ID        string   `json:"id" ulid:"required"`
	ProjectID string   `json:"projectId" ulid:"required"`
	Name      string   `json:"name"`
	Scopes    []string `json:"scopes"`
	// DatabaseID is the only database the key reaches; empty for every
	// database of the project.
	DatabaseID string    `json:"databaseId" ulid:"optional"`
	LastFour   string    `json:"lastFour"`
	CreatedAt  time.Time `json:"createdAt"`
}

// ProjectAPIKeyRequest describes a key to issue: Name is 1-128 characters and
// Scopes a non-empty subset of documents:read and documents:write. An empty
// DatabaseID reaches every database of the project.
type ProjectAPIKeyRequest struct {
	Name       string   `json:"name"`
	Scopes     []string `json:"scopes"`
	DatabaseID string   `json:"databaseId,omitempty" ulid:"optional"`
}

// CreatedProjectAPIKey is a new key with its secret, returned only once.
type CreatedProjectAPIKey struct {
	Key    ProjectAPIKey `json:"key"`
	Secret string        `json:"secret"`
}

// Member is a user's membership in an organization.
type Member struct {
	UserID   string    `json:"userId" ulid:"required"`
	Role     Role      `json:"role"`
	JoinedAt time.Time `json:"joinedAt"`
}

// Invitation is an invitation; a pending one expires after 7 days.
type Invitation struct {
	ID        string           `json:"id" ulid:"required"`
	Email     string           `json:"email"`
	Role      Role             `json:"role"`
	InvitedBy string           `json:"invitedBy" ulid:"required"`
	CreatedAt time.Time        `json:"createdAt"`
	ExpiresAt time.Time        `json:"expiresAt"`
	Status    InvitationStatus `json:"status"`
}

// InvitationStatus is the state of an invitation.
type InvitationStatus string

// Invitation states.
const (
	InvitationPending  InvitationStatus = "pending"
	InvitationAccepted InvitationStatus = "accepted"
	InvitationDeclined InvitationStatus = "declined"
	InvitationRevoked  InvitationStatus = "revoked"
)

// CreatedInvitation is a new invitation with its token, returned only once.
type CreatedInvitation struct {
	Invitation Invitation `json:"invitation"`
	Token      string     `json:"token"`
}

// OwnershipTransfer is an organization's pending ownership transfer.
type OwnershipTransfer struct {
	FromUserID string    `json:"fromUserId" ulid:"required"`
	ToUserID   string    `json:"toUserId" ulid:"required"`
	CreatedAt  time.Time `json:"createdAt"`
	ExpiresAt  time.Time `json:"expiresAt"`
}

// AuditEvent is one recorded access change.
type AuditEvent struct {
	ID         string    `json:"id" ulid:"required"`
	Actor      string    `json:"actor" ulid:"required"`
	Action     string    `json:"action"`
	Target     string    `json:"target"`
	Detail     string    `json:"detail"`
	OccurredAt time.Time `json:"occurredAt"`
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
		path:       append([]string{"v1", "management"}, path...),
		query:      query,
		header:     u.header,
		token:      u.token,
		replaySafe: method == http.MethodGet,
	}
	_, err := u.c.call(ctx, req, nil, out)
	return err
}

func orgPath(org string, extra ...string) []string {
	return append([]string{"organizations", org}, extra...)
}

func projectPath(org, project string, extra ...string) []string {
	return orgPath(org, append([]string{"projects", project}, extra...)...)
}

func databasePath(org, project, databaseID string, extra ...string) []string {
	return projectPath(org, project, append([]string{"databases", databaseID}, extra...)...)
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
	return send[Organization](ctx, u, http.MethodPost, []string{"organizations"}, nameBody{name})
}

// Organizations lists the user's organizations.
func (u *ManagementUser) Organizations(ctx context.Context, opts PageOptions) (*Page[Organization], error) {
	return get[Page[Organization]](ctx, u, []string{"organizations"}, opts.query())
}

// Organization reads an organization.
func (u *ManagementUser) Organization(ctx context.Context, org string) (*Organization, error) {
	return get[Organization](ctx, u, orgPath(org), nil)
}

// RenameOrganization renames an organization; it needs the admin role.
func (u *ManagementUser) RenameOrganization(ctx context.Context, org, name string) (*Organization, error) {
	return send[Organization](ctx, u, http.MethodPatch, orgPath(org), nameBody{name})
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

// CurrentOrganization reads the organization the user last switched to.
func (u *ManagementUser) CurrentOrganization(ctx context.Context) (*Organization, error) {
	return get[Organization](ctx, u, []string{"current-organization"}, nil)
}

// SetCurrentOrganization switches the user to one of their organizations.
func (u *ManagementUser) SetCurrentOrganization(ctx context.Context, org string) (*Organization, error) {
	return send[Organization](ctx, u, http.MethodPut, []string{"current-organization"}, map[string]string{"organizationId": org})
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

// RenameProject renames a project.
func (u *ManagementUser) RenameProject(ctx context.Context, org, project, name string) (*Project, error) {
	return send[Project](ctx, u, http.MethodPatch, projectPath(org, project), nameBody{name})
}

// DeleteProject deletes a project without databases.
func (u *ManagementUser) DeleteProject(ctx context.Context, org, project string) error {
	return u.call(ctx, http.MethodDelete, projectPath(org, project), nil, nil, nil)
}

// ProjectRegistration holds one immutable Management commit for a replica
// region. Reuse it after an uncertain outcome; preparing again creates a new ID.
type ProjectRegistration struct {
	user *ManagementUser
	body json.RawMessage
}

// Commit submits the captured registration. Retrying this handle sends the same
// ULID and byte-identical body without repeating the existence checks.
func (r *ProjectRegistration) Commit(ctx context.Context) error {
	if r == nil || r.user == nil {
		return errors.New("terrabase: project registration was not prepared")
	}
	if r.body == nil {
		return nil
	}
	return r.user.call(ctx, http.MethodPost, []string{"commits"}, nil, r.body, nil)
}

// PrepareProjectRegistration captures the missing organization/project creation
// operations under their IDs and names from another region (TS-643). It makes no
// commit. Call Commit on the returned handle and reuse it for any retries.
// Supplied IDs require the service credential with As on the admin listener.
func (u *ManagementUser) PrepareProjectRegistration(ctx context.Context, org Organization, project Project) (*ProjectRegistration, error) {
	type op struct {
		Op           string `json:"op"`
		Organization string `json:"organization,omitempty"`
		ID           string `json:"id" ulid:"required"`
		Name         string `json:"name"`
	}
	var ops []op
	for _, check := range []struct {
		get func() error
		op  op
	}{
		{func() error { _, err := u.Organization(ctx, org.ID); return err }, op{Op: "create_organization", ID: org.ID, Name: org.Name}},
		{func() error { _, err := u.Project(ctx, org.ID, project.ID); return err }, op{Op: "create_project", Organization: org.ID, ID: project.ID, Name: project.Name}},
	} {
		switch err := check.get(); {
		case errors.Is(err, ErrNotFound):
			ops = append(ops, check.op)
		case err != nil:
			return nil, err
		}
	}
	registration := &ProjectRegistration{user: u}
	if len(ops) == 0 {
		return registration, nil
	}
	commit := struct {
		ID  string `json:"id" ulid:"required"`
		Ops []op   `json:"ops"`
	}{NewID(), ops}
	body, err := json.Marshal(commit)
	if err != nil {
		return nil, err
	}
	registration.body = body
	return registration, nil
}

// CreateDatabase creates an empty database in a project, homed in the
// called region, with copies in every other current serving region.
// A writable database requires at least two regions.
func (u *ManagementUser) CreateDatabase(ctx context.Context, org, project, name string) (*ManagedDatabase, error) {
	return u.CreateDatabaseIn(ctx, org, project, name, Placement{})
}

// CreateDatabaseIn creates an empty database in a project with a placement.
func (u *ManagementUser) CreateDatabaseIn(ctx context.Context, org, project, name string, placement Placement) (*ManagedDatabase, error) {
	body := struct {
		nameBody
		Placement
	}{nameBody{name}, placement}
	return send[ManagedDatabase](ctx, u, http.MethodPost, projectPath(org, project, "databases"), body)
}

// SetDatabaseRegions replaces a database's replica regions with
// replicaRegions, the complete new list of region ids; its home region
// never changes. A writable database needs at least one replica besides the home.
// An empty list is allowed only as transient teardown immediately before deletion;
// document writes then return 409 placement_needs_two_regions.
func (u *ManagementUser) SetDatabaseRegions(ctx context.Context, org, project, databaseID string, replicaRegions []string) (*ManagedDatabase, error) {
	body := struct {
		ReplicaRegions []string `json:"replicaRegions"`
	}{append([]string{}, replicaRegions...)} // [] rather than null
	return send[ManagedDatabase](ctx, u, http.MethodPatch, databasePath(org, project, databaseID), body)
}

// SetDatabaseAllRegions places a full copy of a database in every serving
// region, including regions that join later. SetDatabaseRegions turns it off.
func (u *ManagementUser) SetDatabaseAllRegions(ctx context.Context, org, project, databaseID string) (*ManagedDatabase, error) {
	return send[ManagedDatabase](ctx, u, http.MethodPatch, databasePath(org, project, databaseID), allRegionsBody)
}

// allRegionsBody is the PATCH body that turns allRegions on.
var allRegionsBody = struct {
	AllRegions bool `json:"allRegions"`
}{true}

// Databases lists a project's databases.
func (u *ManagementUser) Databases(ctx context.Context, org, project string, opts PageOptions) (*Page[ManagedDatabase], error) {
	return get[Page[ManagedDatabase]](ctx, u, projectPath(org, project, "databases"), opts.query())
}

// Database reads a database by id.
func (u *ManagementUser) Database(ctx context.Context, org, project, databaseID string) (*ManagedDatabase, error) {
	return get[ManagedDatabase](ctx, u, databasePath(org, project, databaseID), nil)
}

// DeleteDatabase deletes a database, by id, and its data; its branches stay.
// It fails with ErrDatabaseReplicated while the database has replica regions.
// Call SetDatabaseRegions with an empty list, then delete immediately. That empty
// update is transient teardown; document writes return 409 placement_needs_two_regions.
func (u *ManagementUser) DeleteDatabase(ctx context.Context, org, project, databaseID string) error {
	return u.call(ctx, http.MethodDelete, databasePath(org, project, databaseID), nil, nil, nil)
}

// Collections lists every collection of a database, in name order, with its
// live document count and bytes.
func (u *ManagementUser) Collections(ctx context.Context, org, project, databaseID string) ([]CollectionStats, error) {
	out, err := get[struct {
		Items []CollectionStats `json:"items"`
	}](ctx, u, databasePath(org, project, databaseID, "collections"), nil)
	if err != nil {
		return nil, err
	}
	return out.Items, nil
}

// DatabaseUsage reads a database's storage, documents, reads and writes.
func (u *ManagementUser) DatabaseUsage(ctx context.Context, org, project, databaseID string) (*DatabaseUsage, error) {
	return get[DatabaseUsage](ctx, u, databasePath(org, project, databaseID, "usage"), nil)
}

// SQL runs a read-only statement of the dashboard SQL subset (SELECT … FROM
// collection [WHERE …] [ORDER BY …] [LIMIT 1 to 1000]) against a database.
// Invalid SQL fails with ErrInvalidRequest.
func (u *ManagementUser) SQL(ctx context.Context, org, project, databaseID, statement string) (*SQLResult, error) {
	return send[SQLResult](ctx, u, http.MethodPost, databasePath(org, project, databaseID, "sql"), struct {
		SQL string `json:"sql"`
	}{statement})
}

// BranchDatabase creates the database name in the same project, holding a
// consistent snapshot copy of the database databaseID.
func (u *ManagementUser) BranchDatabase(ctx context.Context, org, project, databaseID, name string) (*ManagedDatabase, error) {
	return send[ManagedDatabase](ctx, u, http.MethodPost, databasePath(org, project, databaseID, "branches"), nameBody{name})
}

// CreateIndex creates a secondary index; vector builds may return state building. A viewer cannot. An
// index with the name, or on the same fields of the collection, fails with
// ErrIndexExists.
func (u *ManagementUser) CreateIndex(ctx context.Context, org, project, databaseID string, req IndexRequest) (*Index, error) {
	if err := req.validate(); err != nil {
		return nil, err
	}
	return send[Index](ctx, u, http.MethodPost, databasePath(org, project, databaseID, "indexes"), req)
}

// Indexes lists every secondary index of a database, in name order.
func (u *ManagementUser) Indexes(ctx context.Context, org, project, databaseID string) ([]Index, error) {
	out, err := get[struct {
		Items []Index `json:"items"`
	}](ctx, u, databasePath(org, project, databaseID, "indexes"), nil)
	if err != nil {
		return nil, err
	}
	return out.Items, nil
}

// DropIndex drops a secondary index by name; a viewer cannot.
func (u *ManagementUser) DropIndex(ctx context.Context, org, project, databaseID, name string) error {
	return u.call(ctx, http.MethodDelete, databasePath(org, project, databaseID, "indexes", name), nil, nil, nil)
}

// CreateProjectAPIKey issues a data API key of a project, a Config.Token for
// New; a viewer cannot.
func (u *ManagementUser) CreateProjectAPIKey(ctx context.Context, org, project string, req ProjectAPIKeyRequest) (*CreatedProjectAPIKey, error) {
	return send[CreatedProjectAPIKey](ctx, u, http.MethodPost, projectPath(org, project, "api-keys"), req)
}

// ProjectAPIKeys lists a project's data API keys in creation order.
func (u *ManagementUser) ProjectAPIKeys(ctx context.Context, org, project string, opts PageOptions) (*Page[ProjectAPIKey], error) {
	return get[Page[ProjectAPIKey]](ctx, u, projectPath(org, project, "api-keys"), opts.query())
}

// RevokeProjectAPIKey revokes a project's data API key; it stops
// authenticating immediately. A viewer cannot.
func (u *ManagementUser) RevokeProjectAPIKey(ctx context.Context, org, project, key string) error {
	return u.call(ctx, http.MethodDelete, projectPath(org, project, "api-keys", key), nil, nil, nil)
}

// Members lists an organization's members in user id order.
func (u *ManagementUser) Members(ctx context.Context, org string, opts PageOptions) (*Page[Member], error) {
	return get[Page[Member]](ctx, u, orgPath(org, "members"), opts.query())
}

// SetMemberRole changes a member's role; it needs admin, and owner when the
// member is or becomes an owner.
func (u *ManagementUser) SetMemberRole(ctx context.Context, org, user string, role Role) (*Member, error) {
	return send[Member](ctx, u, http.MethodPut, orgPath(org, "members", user, "role"), map[string]Role{"role": role})
}

// AddMember adds user to an organization as a member, recording email in the
// audit log; a member is returned unchanged. It needs the admin role and the
// management service credential: As, on the admin listener only.
func (u *ManagementUser) AddMember(ctx context.Context, org, user, email string) (*Member, error) {
	return send[Member](ctx, u, http.MethodPut, orgPath(org, "members", user), map[string]string{"email": email})
}

// RemoveMember removes a member; it needs admin, and owner to remove an owner.
func (u *ManagementUser) RemoveMember(ctx context.Context, org, user string) error {
	return u.call(ctx, http.MethodDelete, orgPath(org, "members", user), nil, nil, nil)
}

// Leave removes the user from an organization. The last owner cannot leave.
func (u *ManagementUser) Leave(ctx context.Context, org string) error {
	return u.call(ctx, http.MethodPost, orgPath(org, "leave"), nil, nil, nil)
}

// CreateInvitation invites email with role (empty means member). TerraBase
// sends no email: deliver the returned token yourself.
func (u *ManagementUser) CreateInvitation(ctx context.Context, org, email string, role Role) (*CreatedInvitation, error) {
	body := struct {
		Email string `json:"email"`
		Role  Role   `json:"role,omitempty"`
	}{email, role}
	return send[CreatedInvitation](ctx, u, http.MethodPost, orgPath(org, "invitations"), body)
}

// Invitations lists every invitation with its status, in creation order.
func (u *ManagementUser) Invitations(ctx context.Context, org string, opts PageOptions) (*Page[Invitation], error) {
	return get[Page[Invitation]](ctx, u, orgPath(org, "invitations"), opts.query())
}

// RevokeInvitation revokes an invitation; its token stops working.
func (u *ManagementUser) RevokeInvitation(ctx context.Context, org, invitation string) error {
	return u.call(ctx, http.MethodDelete, orgPath(org, "invitations", invitation), nil, nil, nil)
}

// AcceptInvitation makes the user a member through token. It needs a
// WithToken user whose access token carries a verified email matching the
// invitation's.
func (u *ManagementUser) AcceptInvitation(ctx context.Context, token string) (*Member, error) {
	return send[Member](ctx, u, http.MethodPost, []string{"invitations", "accept"}, map[string]string{"token": token})
}

// OwnershipTransfer reads the pending ownership transfer.
func (u *ManagementUser) OwnershipTransfer(ctx context.Context, org string) (*OwnershipTransfer, error) {
	return get[OwnershipTransfer](ctx, u, orgPath(org, "ownership-transfer"), nil)
}

// StartOwnershipTransfer offers ownership to a member who is not an owner,
// replacing a pending offer; it needs the owner role.
func (u *ManagementUser) StartOwnershipTransfer(ctx context.Context, org, toUser string) (*OwnershipTransfer, error) {
	return send[OwnershipTransfer](ctx, u, http.MethodPost, orgPath(org, "ownership-transfer"), map[string]string{"userId": toUser})
}

// CancelOwnershipTransfer cancels the pending transfer; the owner or the
// nominee may cancel.
func (u *ManagementUser) CancelOwnershipTransfer(ctx context.Context, org string) error {
	return u.call(ctx, http.MethodDelete, orgPath(org, "ownership-transfer"), nil, nil, nil)
}

// ConfirmOwnershipTransfer accepts the transfer as its nominee and returns the
// nominee's membership, now owner.
func (u *ManagementUser) ConfirmOwnershipTransfer(ctx context.Context, org string) (*Member, error) {
	return send[Member](ctx, u, http.MethodPost, orgPath(org, "ownership-transfer", "confirm"), nil)
}

// AuditRetention returns how many days audit events are kept (1-90).
func (u *ManagementUser) AuditRetention(ctx context.Context, org string) (int, error) {
	r, err := get[auditRetention](ctx, u, orgPath(org, "audit-retention"), nil)
	if err != nil {
		return 0, err
	}
	return r.Days, nil
}

// SetAuditRetention sets how many days audit events are kept (1-90).
func (u *ManagementUser) SetAuditRetention(ctx context.Context, org string, days int) (int, error) {
	r, err := send[auditRetention](ctx, u, http.MethodPut, orgPath(org, "audit-retention"), auditRetention{days})
	if err != nil {
		return 0, err
	}
	return r.Days, nil
}

type auditRetention struct {
	Days int `json:"days"`
}

// AuditEvents lists audit events, newest first, within the retention.
func (u *ManagementUser) AuditEvents(ctx context.Context, org string, opts PageOptions) (*Page[AuditEvent], error) {
	return get[Page[AuditEvent]](ctx, u, orgPath(org, "audit-events"), opts.query())
}
