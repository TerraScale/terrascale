package v1

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"mime"
	"net/http"
	"net/url"
	"strings"
	"time"
)

// DatabaseRequestLog is a completed authorized request. Route is a redacted
// route template, never a URL, document key, query text or credential.
type DatabaseRequestLog struct {
	ID         string    `json:"id"`
	Time       time.Time `json:"time"`
	Region     string    `json:"region"`
	Method     string    `json:"method"`
	Route      string    `json:"route"`
	Status     int       `json:"status"`
	LatencyMs  float64   `json:"latencyMs"`
	Operations int64     `json:"operations"`
	IsQuery    bool      `json:"isQuery"`
	// RequestID is an optional correlation identifier, not an entity ULID.
	RequestID *string `json:"requestId"`
}

// DatabaseRequestLogs contains at most the newest 100 recorded requests within
// 24 hours. Recording is best effort; it is not a durable export or cursor page.
type DatabaseRequestLogs struct {
	DatabaseID string               `json:"databaseId"`
	Items      []DatabaseRequestLog `json:"items"`
	// Truncated reports a full recent window, not a continuation token.
	Truncated bool `json:"truncated"`
	// RecordingLimited reports capacity omissions on the answering server
	// since its startup. It is not a durable or global loss counter.
	RecordingLimited bool `json:"recordingLimited"`
}

var errRequestLogs = errors.New("terrabase: invalid database request logs response")

func requireLogFields(data []byte, names ...string) error {
	var fields map[string]json.RawMessage
	if json.Unmarshal(data, &fields) != nil {
		return errRequestLogs
	}
	for _, name := range names {
		value := fields[name]
		if len(value) == 0 || bytes.Equal(bytes.TrimSpace(value), []byte("null")) {
			return errRequestLogs
		}
	}
	return nil
}

// UnmarshalJSON validates required fields before replacing the receiver.
func (l *DatabaseRequestLog) UnmarshalJSON(data []byte) error {
	if err := requireLogFields(data, "id", "time", "region", "method", "route", "status", "latencyMs", "operations", "isQuery"); err != nil {
		return err
	}
	type plain DatabaseRequestLog
	var next plain
	if json.Unmarshal(data, &next) != nil || checkID(next.ID) != nil || next.Time.IsZero() || next.Status < 100 || next.Status > 599 || next.LatencyMs < 0 || next.Operations < 0 {
		return errRequestLogs
	}
	*l = DatabaseRequestLog(next)
	return nil
}

// UnmarshalJSON preserves false/zero values and rejects missing/null required
// members and null entries. Failure leaves the previous receiver unchanged.
func (l *DatabaseRequestLogs) UnmarshalJSON(data []byte) error {
	if err := requireLogFields(data, "databaseId", "items", "truncated", "recordingLimited"); err != nil {
		return err
	}
	type plain DatabaseRequestLogs
	var next plain
	if json.Unmarshal(data, &next) != nil || checkID(next.DatabaseID) != nil || len(next.Items) > 100 {
		return errRequestLogs
	}
	*l = DatabaseRequestLogs(next)
	return nil
}

// RequestLogs reads recent best-effort database observations using a project
// token with databases:read. Status is empty (all) or 2xx, 3xx, 4xx or 5xx.
// Pending records may be lost on crash; remote records arrive by replication.
// A query request with LatencyMs >= 100 is a slow query. There is no cursor,
// configurable limit or guarantee that a just-completed request is recorded.
func (d *Database) RequestLogs(ctx context.Context, status string) (*DatabaseRequestLogs, error) {
	if status != "" && status != "2xx" && status != "3xx" && status != "4xx" && status != "5xx" {
		return nil, errors.New("terrabase: request log status must be empty, 2xx, 3xx, 4xx or 5xx")
	}
	q := url.Values{}
	if status != "" {
		q.Set("status", status)
	}
	p, err := d.path("logs")
	if err != nil {
		return nil, err
	}
	var out DatabaseRequestLogs
	response, err := d.c.call(ctx, request{method: http.MethodGet, path: p, query: q, replaySafe: true}, nil, &out)
	if err != nil {
		return nil, err
	}
	media, _, mediaErr := mime.ParseMediaType(response.header.Get("Content-Type"))
	if response.status != http.StatusOK || mediaErr != nil || !strings.EqualFold(media, "application/json") {
		return nil, errRequestLogs
	}
	return &out, nil
}
