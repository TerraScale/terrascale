package sdk

import (
	"context"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"
)

// MaxOperationPageSize is the largest operation discovery page accepted by
// the public API.
const MaxOperationPageSize = 50

// OperationPage is a bounded, project-scoped view of the durable operation
// journal. CoveredKinds explicitly identifies which operation owners are
// represented by the page.
type OperationPage struct {
	Operations   []OperationSummary `json:"operations"`
	NextCursor   *string            `json:"next_cursor"`
	CoveredKinds []string           `json:"covered_kinds"`
}

// OperationSummary is an authoritative, read-only operation projection.
type OperationSummary struct {
	OperationID        string    `json:"operation_id"`
	Kind               string    `json:"kind"`
	DatabaseID         string    `json:"database_id"`
	Name               string    `json:"name"`
	State              string    `json:"state"`
	Stage              string    `json:"stage"`
	CreatedAt          time.Time `json:"created_at"`
	Revision           uint64    `json:"revision"`
	Cancellable        bool      `json:"cancellable"`
	CancellationReason string    `json:"cancellation_reason"`
}

// List returns the next page of operations for the client's configured
// project. after is an opaque cursor returned by the preceding page. A zero
// limit uses the service default of 50; nonzero limits must be 1 through 50.
func (o *OperationsClient) List(ctx context.Context, after string, limit int, opts ...RequestOption) (*OperationPage, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	if strings.TrimSpace(o.c.cfg.ProjectID) == "" {
		return nil, &ValidationError{Field: "project_id", Message: "must be configured for operation discovery"}
	}
	if limit < 0 || limit > MaxOperationPageSize {
		return nil, &ValidationError{Field: "limit", Message: "must be between 1 and 50, or zero for the server default"}
	}
	if len([]byte(after)) > 256 {
		return nil, &ValidationError{Field: "after", Message: "must be at most 256 bytes"}
	}
	query := url.Values{}
	if after != "" {
		query.Set("after", after)
	}
	if limit > 0 {
		query.Set("limit", strconv.Itoa(limit))
	}
	var out OperationPage
	if err := o.c.do(ctx, http.MethodGet, "/api/v1/operations", query, nil, &out, "list_operations", opts...); err != nil {
		return nil, err
	}
	if len(out.Operations) > MaxOperationPageSize || (limit > 0 && len(out.Operations) > limit) {
		return nil, &ValidationError{Field: "operations", Message: "response exceeds the requested page limit"}
	}
	return &out, nil
}
