package v1

import (
	"context"
	"errors"
	"net/http"
	"net/url"
	"regexp"
	"strconv"
	"time"
)

// MetricsRange selects a TerraBase window with automatic server-side bucketing.
type MetricsRange string

const (
	Range1Hour   MetricsRange = "1h"
	Range24Hours MetricsRange = "24h"
	Range7Days   MetricsRange = "7d"
	Range30Days  MetricsRange = "30d"
)

// MetricsOptions filters a project's analytics or replication measurements.
// Empty Range uses the server default; empty DatabaseID or Region selects all.
// After is the previous response's Next. Limit is 1-100, or zero for the default.
type MetricsOptions struct {
	Range      MetricsRange
	DatabaseID string
	Region     string
	After      string
	Limit      int
}

// ReplicationStatus is TerraBase's assessment of one database replica.
type ReplicationStatus string

const (
	ReplicationHealthy  ReplicationStatus = "healthy"
	ReplicationLagging  ReplicationStatus = "lagging"
	ReplicationDiverged ReplicationStatus = "diverged"
	ReplicationStopped  ReplicationStatus = "stopped"
)

// ReplicationBytesMeter identifies usage metered separately from client traffic.
const ReplicationBytesMeter = "replication_bytes"

// ReplicationBandwidth measures a directed region pair within the response
// period. Sent and received bytes are separate observations, not additive usage.
type ReplicationBandwidth struct {
	SourceRegion      string `json:"sourceRegion"`
	DestinationRegion string `json:"destinationRegion"`
	BytesSent         int64  `json:"bytesSent"`
	BytesReceived     int64  `json:"bytesReceived"`
	Meter             string `json:"meter"`
}

// ReplicationResponse is a page of database x region measurements for [From, To).
type ReplicationResponse struct {
	ProjectID     string               `json:"projectId"`
	From          time.Time            `json:"from"`
	To            time.Time            `json:"to"`
	BucketSeconds int64                `json:"bucketSeconds"`
	Items         []ReplicationMetrics `json:"items"`
	Next          string               `json:"next"`
}

// ReplicationMetrics includes period counters and the latest replica state.
// Nil observations mean unavailable; measured zero remains distinct.
type ReplicationMetrics struct {
	DatabaseID        string                 `json:"databaseId"`
	Region            string                 `json:"region"`
	ReplicatedOps     int64                  `json:"replicatedOps"`
	Bandwidth         []ReplicationBandwidth `json:"bandwidth"`
	LagMS             *float64               `json:"lagMs"`
	Divergence        *int64                 `json:"divergence"`
	ConvergenceTimeMS *float64               `json:"convergenceTimeMs"`
	LastApplied       *time.Time             `json:"lastApplied"`
	Status            ReplicationStatus      `json:"status"`
}

// AnalyticsResponse is a page of server-bucketed project measurements.
type AnalyticsResponse struct {
	ProjectID     string           `json:"projectId"`
	From          time.Time        `json:"from"`
	To            time.Time        `json:"to"`
	BucketSeconds int64            `json:"bucketSeconds"`
	Series        []AnalyticsPoint `json:"series"`
	Next          string           `json:"next"`
}

// AnalyticsPoint is one bucket per database and region. Counters are int64 to
// preserve exact byte and operation counts. Latencies are milliseconds; nil
// means no samples. TerraBase owns the counters, percentiles and bucketing.
type AnalyticsPoint struct {
	Start                time.Time              `json:"start"`
	DatabaseID           string                 `json:"databaseId"`
	Region               string                 `json:"region"`
	Requests             map[string]int64       `json:"requests"`
	Ops                  map[string]int64       `json:"ops"`
	Errors               int64                  `json:"errors"`
	P50LatencyMS         *float64               `json:"p50LatencyMs"`
	P95LatencyMS         *float64               `json:"p95LatencyMs"`
	P99LatencyMS         *float64               `json:"p99LatencyMs"`
	StorageBytes         int64                  `json:"storageBytes"`
	ClientBytesSent      int64                  `json:"clientBytesSent"`
	ClientBytesReceived  int64                  `json:"clientBytesReceived"`
	ReplicationBandwidth []ReplicationBandwidth `json:"replicationBandwidth"`
}

// Analytics reads GET /projects/{project}/analytics with a ts-auth project
// access token supplied to Management.WithToken. Next pages retain the filters.
func (u *ManagementUser) Analytics(ctx context.Context, project string, opts MetricsOptions) (*AnalyticsResponse, error) {
	var out AnalyticsResponse
	if err := u.metrics(ctx, project, "analytics", opts, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

// Replication reads GET /projects/{project}/replication. Database IDs are plain
// ULIDs; an empty filter selects all databases. Use Management.WithToken with
// a ts-auth project access token. No usage is computed by the client.
func (u *ManagementUser) Replication(ctx context.Context, project string, opts MetricsOptions) (*ReplicationResponse, error) {
	var out ReplicationResponse
	if err := u.metrics(ctx, project, "replication", opts, &out); err != nil {
		return nil, err
	}
	return &out, nil
}

// ULIDs use ASCII base32; Unicode case-fold equivalents are invalid.
var metricsIDPattern = regexp.MustCompile(`^[0-7][0-9A-HJKMNP-TV-Za-hjkmnp-tv-z]{25}$`)

func (u *ManagementUser) metrics(ctx context.Context, project, endpoint string, opts MetricsOptions, out any) error {
	if u.err != nil {
		return u.err
	}
	if u.token == "" {
		return errors.New("terrabase: metrics require WithToken with a ts-auth project access token")
	}
	if !metricsIDPattern.MatchString(project) || (opts.DatabaseID != "" && !metricsIDPattern.MatchString(opts.DatabaseID)) {
		return errors.New("terrabase: metrics project and database IDs must be plain ULIDs")
	}
	switch opts.Range {
	case "", Range1Hour, Range24Hours, Range7Days, Range30Days:
	default:
		return errors.New("terrabase: metrics range must be 1h, 24h, 7d or 30d")
	}
	if opts.Limit < 0 || opts.Limit > 100 {
		return errors.New("terrabase: metrics limit must be 1-100 or zero")
	}
	q := url.Values{}
	for key, value := range map[string]string{"range": string(opts.Range), "database": opts.DatabaseID, "region": opts.Region, "after": opts.After} {
		if value != "" {
			q.Set(key, value)
		}
	}
	if opts.Limit != 0 {
		q.Set("limit", strconv.Itoa(opts.Limit))
	}
	_, err := u.c.call(ctx, request{method: http.MethodGet, path: []string{"projects", project, endpoint}, query: q, header: u.header, token: u.token, replaySafe: true}, nil, out)
	return err
}
