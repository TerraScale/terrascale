package v1

import (
	"context"
	"errors"
	"net/http"
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
