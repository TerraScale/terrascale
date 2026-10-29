package v1

import (
	"context"
	"errors"
	"net/http"
	"net/url"
)

// Databases lists a project's databases using a project access token with
// databases:read. Names in the response address Client.Database.
func (c *Client) Databases(ctx context.Context, project string, opts PageOptions) (*Page[ManagedDatabase], error) {
	if err := checkID(project); err != nil {
		return nil, err
	}
	var out Page[ManagedDatabase]
	_, err := c.call(ctx, request{method: http.MethodGet, path: []string{"v1", "projects", project, "databases"}, query: opts.query(), replaySafe: true}, nil, &out)
	if err != nil {
		return nil, err
	}
	if out.Items == nil {
		return nil, errors.New("terrabase: databases response requires items array")
	}
	return &out, nil
}

// Info reads database metadata by name using a project access token with
// databases:read. The server enforces project and optional database restrictions.
func (d *Database) Info(ctx context.Context) (*ManagedDatabase, error) {
	p, err := d.path()
	if err != nil {
		return nil, err
	}
	var out ManagedDatabase
	_, err = d.c.call(ctx, request{method: http.MethodGet, path: p, replaySafe: true}, nil, &out)
	if err != nil {
		return nil, err
	}
	return &out, nil
}

// SQLCheck describes a SQL validation or explain result.
type SQLCheck struct {
	OK          bool              `json:"ok"`
	Error       *string           `json:"error"`
	Collection  *string           `json:"collection"`
	HasLimit    bool              `json:"hasLimit"`
	Plan        []string          `json:"plan"`
	Indexes     []string          `json:"indexes"`
	Suggestions []IndexSuggestion `json:"suggestions"`
}

// IndexSuggestion identifies a filtered or sorted field lacking a leading index.
type IndexSuggestion struct {
	Field  string `json:"field"`
	Reason string `json:"reason"`
}

// SQL runs the server's read-only SELECT subset using databases:read. It does
// not support arbitrary SQL or mutations. The default LIMIT is 100, at most
// 1000; results also stop at 8 MiB. SQL is validated by the server.
func (d *Database) SQL(ctx context.Context, statement string) (*SQLResult, error) {
	var out SQLResult
	if err := d.sql(ctx, statement, "", &out); err != nil {
		return nil, err
	}
	if out.Rows == nil {
		return nil, errors.New("terrabase: SQL response requires rows array")
	}
	return &out, nil
}

// CheckSQL validates and plans SQL without running it. SQL outside the subset
// returns OK false with Error; request or authorization failures return errors.
func (d *Database) CheckSQL(ctx context.Context, statement string) (*SQLCheck, error) {
	return d.sqlCheck(ctx, statement, "check")
}

// ExplainSQL returns the engine plan without executing or metering document
// reads. Requires databases:read; invalid SQL returns an API error.
func (d *Database) ExplainSQL(ctx context.Context, statement string) (*SQLCheck, error) {
	return d.sqlCheck(ctx, statement, "explain")
}

func (d *Database) sqlCheck(ctx context.Context, statement, endpoint string) (*SQLCheck, error) {
	var out struct {
		SQLCheck
		OK *bool `json:"ok"`
	}
	if err := d.sql(ctx, statement, endpoint, &out); err != nil {
		return nil, err
	}
	if out.OK == nil {
		return nil, errors.New("terrabase: SQL check response requires ok boolean")
	}
	out.SQLCheck.OK = *out.OK
	return &out.SQLCheck, nil
}

func (d *Database) sql(ctx context.Context, statement, endpoint string, out any) error {
	p, err := d.path("sql")
	if err != nil {
		return err
	}
	if endpoint != "" {
		p = append(p, endpoint)
	}
	_, err = d.c.call(ctx, request{method: http.MethodPost, path: p, replaySafe: true, readOnly: true}, struct {
		SQL string `json:"sql"`
	}{statement}, out)
	return err
}

// projectRequest addresses the public project hierarchy; selectors remain names.
func projectRequest(method, project string, query url.Values, extra ...string) (request, error) {
	if err := checkID(project); err != nil {
		return request{}, err
	}
	return request{method: method, path: append([]string{"v1", "projects", project}, extra...), query: query, replaySafe: method == http.MethodGet}, nil
}

func publicCall[T any](ctx context.Context, c *Client, req request, in any) (*T, error) {
	var out T
	if _, err := c.call(ctx, req, in, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

func databaseCall[T any](ctx context.Context, d *Database, method string, query url.Values, in any, extra ...string) (*T, error) {
	p, err := d.path(extra...)
	if err != nil {
		return nil, err
	}
	return publicCall[T](ctx, d.c, request{method: method, path: p, query: query, replaySafe: method == http.MethodGet}, in)
}

// CreateIn creates this database with explicit placement. A writable database
// requires at least two regions. Nil ReplicaRegions lets the server select every
// other current serving region; an empty list is sent explicitly and returns
// 400 placement_needs_two_regions. A non-empty list must include at least one
// region other than the home. AllRegions also follows future regions.
func (d *Database) CreateIn(ctx context.Context, placement Placement) (*ManagedDatabase, error) {
	if _, err := d.path(); err != nil {
		return nil, err
	}
	req, err := projectRequest(http.MethodPost, d.project, nil, "databases")
	if err != nil {
		return nil, err
	}
	body := struct {
		Name           string   `json:"name"`
		HomeRegion     string   `json:"homeRegion,omitempty"`
		ReplicaRegions []string `json:"replicaRegions"`
		AllRegions     bool     `json:"allRegions,omitempty"`
	}{d.name, placement.HomeRegion, placement.ReplicaRegions, placement.AllRegions}
	return publicCall[ManagedDatabase](ctx, d.c, req, body)
}

// SetRegions replaces the database's replica regions from its home region.
// A writable database needs at least one replica besides the home. An empty slice
// can temporarily make document writes return 409 placement_needs_two_regions;
// it is not required for deletion and does not provide supported single-region placement. The home cannot change.
func (d *Database) SetRegions(ctx context.Context, regions []string) (*ManagedDatabase, error) {
	if regions == nil {
		regions = []string{}
	}
	return databaseCall[ManagedDatabase](ctx, d, http.MethodPatch, nil, struct {
		ReplicaRegions []string `json:"replicaRegions"`
	}{regions})
}

// SetAllRegions places a full copy of the database in every serving region,
// including regions that join later. SetRegions turns it off.
func (d *Database) SetAllRegions(ctx context.Context) (*ManagedDatabase, error) {
	return databaseCall[ManagedDatabase](ctx, d, http.MethodPatch, nil, allRegionsBody)
}

// Branch clones this database into a new database of the same project.
// Later changes to either database are independent.
func (d *Database) Branch(ctx context.Context, name string) (*ManagedDatabase, error) {
	if err := checkName("database", name); err != nil {
		return nil, err
	}
	return databaseCall[ManagedDatabase](ctx, d, http.MethodPost, nil, nameBody{name}, "branches")
}
