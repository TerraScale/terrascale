package v1

import (
	"context"
	"net/http"
	"net/url"
	"time"
)

// SavedQuery is a named SQL statement in a project.
type SavedQuery struct {
	ID        string     `json:"id" ulid:"required"`
	Name      string     `json:"name"`
	SQL       string     `json:"sql"`
	CreatedAt time.Time  `json:"createdAt"`
	UpdatedAt *time.Time `json:"updatedAt"`
}

// SavedQueryRequest creates or replaces a saved statement.
type SavedQueryRequest struct {
	Name string `json:"name"`
	SQL  string `json:"sql"`
}

// QueryShapeStats is an observed query with its literal values removed.
type QueryShapeStats struct {
	ID             string    `json:"id"`
	Collection     string    `json:"collection"`
	Shape          string    `json:"shape"`
	Queries        int64     `json:"queries"`
	TotalLatencyMS float64   `json:"totalLatencyMs"`
	MaxLatencyMS   float64   `json:"maxLatencyMs"`
	RowsScanned    int64     `json:"rowsScanned"`
	LastSeenAt     time.Time `json:"lastSeenAt"`
}

func projectCall[T any](ctx context.Context, c *Client, method, project string, query url.Values, in any, extra ...string) (*T, error) {
	req, err := projectRequest(method, project, query, extra...)
	if err != nil {
		return nil, err
	}
	return publicCall[T](ctx, c, req, in)
}

// CreateSavedQuery saves a valid statement in the project.
func (c *Client) CreateSavedQuery(ctx context.Context, project string, in SavedQueryRequest) (*SavedQuery, error) {
	return projectCall[SavedQuery](ctx, c, http.MethodPost, project, nil, in, "saved-queries")
}

// SavedQueries lists statements in creation order with management pagination.
func (c *Client) SavedQueries(ctx context.Context, project string, opts PageOptions) (*Page[SavedQuery], error) {
	return projectCall[Page[SavedQuery]](ctx, c, http.MethodGet, project, opts.query(), nil, "saved-queries")
}

// SavedQuery reads a statement by its canonical ULID.
func (c *Client) SavedQuery(ctx context.Context, project, id string) (*SavedQuery, error) {
	if err := checkID(id); err != nil {
		return nil, err
	}
	return projectCall[SavedQuery](ctx, c, http.MethodGet, project, nil, nil, "saved-queries", id)
}

// UpdateSavedQuery replaces both the statement name and SQL.
func (c *Client) UpdateSavedQuery(ctx context.Context, project, id string, in SavedQueryRequest) (*SavedQuery, error) {
	if err := checkID(id); err != nil {
		return nil, err
	}
	return projectCall[SavedQuery](ctx, c, http.MethodPut, project, nil, in, "saved-queries", id)
}

// DeleteSavedQuery deletes a statement by ULID.
func (c *Client) DeleteSavedQuery(ctx context.Context, project, id string) error {
	if err := checkID(id); err != nil {
		return err
	}
	req, err := projectRequest(http.MethodDelete, project, nil, "saved-queries", id)
	if err != nil {
		return err
	}
	_, err = c.call(ctx, req, nil, nil)
	return err
}

// QueryShapes lists the producer's bounded workload statistics.
func (d *Database) QueryShapes(ctx context.Context) ([]QueryShapeStats, error) {
	out, err := databaseCall[struct {
		Items []QueryShapeStats `json:"items"`
	}](ctx, d, http.MethodGet, nil, nil, "query-shapes")
	if err != nil {
		return nil, err
	}
	return out.Items, nil
}
