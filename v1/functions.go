package v1

import (
	"context"
	"errors"
	"net/http"
	"net/url"
	"strconv"
	"time"
)

// FunctionTrigger runs a function on committed changes to Collection.
// Events is upsert and/or delete; empty means both.
type FunctionTrigger struct {
	Collection string   `json:"collection"`
	Events     []string `json:"events,omitempty"`
}

// Function is a deployed JavaScript function. Lists leave Source empty.
type Function struct {
	Name      string           `json:"name"`
	Source    string           `json:"source"`
	Trigger   *FunctionTrigger `json:"trigger"`
	Cron      *string          `json:"cron"`
	CreatedAt time.Time        `json:"createdAt"`
	UpdatedAt time.Time        `json:"updatedAt"`
}

// FunctionRequest deploys a function. Cron is a five-field UTC expression;
// empty means no schedule. A nil Trigger means no change trigger.
type FunctionRequest struct {
	Source  string
	Trigger *FunctionTrigger
	Cron    string
}

// FunctionLog is one retained invocation, newest first. Trigger is http,
// change or cron; Status is ok or error.
type FunctionLog struct {
	ID         string    `json:"id" ulid:"required"`
	At         time.Time `json:"at"`
	Trigger    string    `json:"trigger"`
	Status     string    `json:"status"`
	DurationMs int64     `json:"durationMs"`
	Lines      []string  `json:"lines"`
	Error      *string   `json:"error"`
}

// DeployFunction creates or replaces the named function.
// Needs both documents:read and documents:write.
func (d *Database) DeployFunction(ctx context.Context, name string, in FunctionRequest) (*Function, error) {
	if err := checkName("function", name); err != nil {
		return nil, err
	}
	if in.Trigger != nil {
		if err := checkName("collection", in.Trigger.Collection); err != nil {
			return nil, err
		}
	}
	body := struct {
		Source  string           `json:"source"`
		Trigger *FunctionTrigger `json:"trigger"`
		Cron    *string          `json:"cron"`
	}{Source: in.Source, Trigger: in.Trigger}
	if in.Cron != "" {
		body.Cron = &in.Cron
	}
	return databaseCall[Function](ctx, d, http.MethodPut, nil, body, "functions", name)
}

// Functions lists the database's functions without their source. Needs documents:read.
func (d *Database) Functions(ctx context.Context) ([]Function, error) {
	out, err := databaseCall[[]Function](ctx, d, http.MethodGet, nil, nil, "functions")
	if err != nil {
		return nil, err
	}
	return *out, nil
}

// Function reads one function with its source. Needs documents:read.
func (d *Database) Function(ctx context.Context, name string) (*Function, error) {
	if err := checkName("function", name); err != nil {
		return nil, err
	}
	return databaseCall[Function](ctx, d, http.MethodGet, nil, nil, "functions", name)
}

// DeleteFunction deletes the function with its trigger, schedule and logs.
// Needs both documents:read and documents:write.
func (d *Database) DeleteFunction(ctx context.Context, name string) error {
	if err := checkName("function", name); err != nil {
		return err
	}
	p, err := d.path("functions", name)
	if err != nil {
		return err
	}
	_, err = d.c.call(ctx, request{method: http.MethodDelete, path: p}, nil, nil)
	return err
}

// InvokeFunction runs the function with body (JSON, or nil for none) as
// event.body and returns the handler's response body. A handler status
// outside 2xx is an *APIError whose Body is the handler's body. Invocations
// may write documents, so they are never retried. Needs documents:read.
func (d *Database) InvokeFunction(ctx context.Context, name string, body []byte) ([]byte, error) {
	if err := checkName("function", name); err != nil {
		return nil, err
	}
	p, err := d.path("functions", name, "invoke")
	if err != nil {
		return nil, err
	}
	resp, err := d.c.do(ctx, request{method: http.MethodPost, path: p, body: body})
	if err != nil {
		return nil, err
	}
	return resp.body, nil
}

// FunctionLogs lists the newest invocations, newest first. Limit is 1..100;
// zero uses the server default. Needs documents:read.
func (d *Database) FunctionLogs(ctx context.Context, name string, limit int) ([]FunctionLog, error) {
	if err := checkName("function", name); err != nil {
		return nil, err
	}
	if limit < 0 || limit > 100 {
		return nil, errors.New("terrabase: function log limit must be 1..100 or zero for the default")
	}
	q := url.Values{}
	if limit != 0 {
		q.Set("limit", strconv.Itoa(limit))
	}
	out, err := databaseCall[[]FunctionLog](ctx, d, http.MethodGet, q, nil, "functions", name, "logs")
	if err != nil {
		return nil, err
	}
	return *out, nil
}

// FunctionDeliveries lists the retained change-trigger deliveries to the
// function. Needs documents:read.
func (d *Database) FunctionDeliveries(ctx context.Context, name string) ([]WebhookDelivery, error) {
	if err := checkName("function", name); err != nil {
		return nil, err
	}
	out, err := databaseCall[[]WebhookDelivery](ctx, d, http.MethodGet, nil, nil, "functions", name, "deliveries")
	if err != nil {
		return nil, err
	}
	return *out, nil
}
