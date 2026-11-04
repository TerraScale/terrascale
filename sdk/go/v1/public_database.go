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

// RenameDatabase renames the database identified by its immutable ULID, using
// databases:write. The server preserves its ID, data, keys and placement, returns
// 400 for an invalid name and 409 for a locally known duplicate. Only the home
// region accepts a rename; others return 421 wrong_region. Concurrent claims
// converge with the smallest ULID keeping the name. After convergence old name
// routes have no alias; update name-based connection strings. ID-based strings stay valid.
// Existing name-bound Database handles are not changed. Mutations are not
// replayed after uncertain failures; read metadata before explicitly retrying.
func (c *Client) RenameDatabase(ctx context.Context, project, databaseID, name string) (*ManagedDatabase, error) {
	if err := checkID(databaseID); err != nil {
		return nil, err
	}
	if err := checkName("database", name); err != nil {
		return nil, err
	}
	req, err := projectRequest(http.MethodPatch, project, nil, "databases", databaseID)
	if err != nil {
		return nil, err
	}
	return publicCall[ManagedDatabase](ctx, c, req, nameBody{name})
}

// Info reads database metadata by current name or immutable ULID using a project access token with
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

// projectRequest addresses the public project hierarchy.
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

// CreateIn creates this database served from region, a region id such as
// gru-1; empty means the called region.
func (d *Database) CreateIn(ctx context.Context, region string) (*ManagedDatabase, error) {
	if err := checkName("database", d.name); err != nil {
		return nil, err
	}
	if _, err := d.path(); err != nil {
		return nil, err
	}
	req, err := projectRequest(http.MethodPost, d.project, nil, "databases")
	if err != nil {
		return nil, err
	}
	body := struct {
		Name       string `json:"name"`
		HomeRegion string `json:"homeRegion,omitempty"`
	}{d.name, region}
	return publicCall[ManagedDatabase](ctx, d.c, req, body)
}

// Branch clones this database into a new database of the same project.
// Later changes to either database are independent.
func (d *Database) Branch(ctx context.Context, name string) (*ManagedDatabase, error) {
	if err := checkName("database", name); err != nil {
		return nil, err
	}
	return databaseCall[ManagedDatabase](ctx, d, http.MethodPost, nil, nameBody{name}, "branches")
}
