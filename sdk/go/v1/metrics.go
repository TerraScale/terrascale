package v1

import (
	"context"
	"errors"
	"fmt"
	"net/http"
	"net/url"
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
type MetricsOptions struct {
	Range      MetricsRange
	DatabaseID string
	Region     string
}

// ReplicationStatus is TerraBase's assessment of one database replica.
type ReplicationStatus string

const (
	ReplicationHealthy  ReplicationStatus = "healthy"
	ReplicationLagging  ReplicationStatus = "lagging"
	ReplicationDiverged ReplicationStatus = "diverged"
	ReplicationStopped  ReplicationStatus = "stopped"
)

// BandwidthUsage separates sent and received observations.
type BandwidthUsage struct {
	BytesSent     int64 `json:"bytesSent"`
	BytesReceived int64 `json:"bytesReceived"`
}

// ReplicationBandwidth measures traffic by directed region pair and kind
// (delta, anti_entropy or handoff).
type ReplicationBandwidth struct {
	SourceRegion      string `json:"sourceRegion"`
	DestinationRegion string `json:"destinationRegion"`
	Kind              string `json:"kind"`
	BytesSent         int64  `json:"bytesSent"`
	BytesReceived     int64  `json:"bytesReceived"`
}

// ReplicationResponse contains observations in automatically bucketed UTC time.
type ReplicationResponse struct {
	ProjectID     string               `json:"projectId" ulid:"required"`
	Range         MetricsRange         `json:"range"`
	From          time.Time            `json:"from"`
	To            time.Time            `json:"to"`
	BucketSeconds int64                `json:"bucketSeconds"`
	Items         []ReplicationMetrics `json:"items"`
}

// ReplicationMetrics includes period counters and latest replica state.
// Nil observations mean unavailable; measured false and zero remain distinct.
type ReplicationMetrics struct {
	DatabaseID    string                 `json:"databaseId" ulid:"required"`
	DatabaseName  string                 `json:"databaseName"`
	Region        string                 `json:"region"`
	Status        ReplicationStatus      `json:"status"`
	ReplicatedOps int64                  `json:"replicatedOps"`
	LagMS         *float64               `json:"lagMs"`
	Divergence    *bool                  `json:"divergence"`
	ConvergenceMS *float64               `json:"convergenceMs"`
	LastApplied   *time.Time             `json:"lastApplied"`
	Bandwidth     []ReplicationBandwidth `json:"bandwidth"`
	Series        []ReplicationPoint     `json:"series"`
}

// ReplicationPoint is a replication period bucket.
type ReplicationPoint struct {
	Start         time.Time              `json:"start"`
	ReplicatedOps int64                  `json:"replicatedOps"`
	Bandwidth     []ReplicationBandwidth `json:"bandwidth"`
}

// AnalyticsResponse groups series by database and observing region.
type AnalyticsResponse struct {
	ProjectID     string              `json:"projectId" ulid:"required"`
	Range         MetricsRange        `json:"range"`
	From          time.Time           `json:"from"`
	To            time.Time           `json:"to"`
	BucketSeconds int64               `json:"bucketSeconds"`
	Items         []DatabaseAnalytics `json:"items"`
}

// DatabaseAnalytics contains one database and region's oldest-first buckets.
type DatabaseAnalytics struct {
	DatabaseID   string           `json:"databaseId" ulid:"required"`
	DatabaseName string           `json:"databaseName"`
	Region       string           `json:"region"`
	Series       []AnalyticsPoint `json:"series"`
}

// LatencyPercentiles contains approximate histogram percentiles in milliseconds.
// Nil means there were no request samples.
type LatencyPercentiles struct {
	P50MS *float64 `json:"p50Ms"`
	P95MS *float64 `json:"p95Ms"`
	P99MS *float64 `json:"p99Ms"`
}

// AnalyticsPoint is one UTC bucket. StorageBytes is the sampled average, or
// nil before sampling. Counters are best-effort observations, not billing.
type AnalyticsPoint struct {
	Start                time.Time           `json:"start"`
	Requests             map[string]int64    `json:"requests"`
	Operations           map[string]int64    `json:"operations"`
	Errors               int64               `json:"errors"`
	Latency              *LatencyPercentiles `json:"latency"`
	StorageBytes         *int64              `json:"storageBytes"`
	ClientBandwidth      *BandwidthUsage     `json:"clientBandwidth"`
	ReplicationBandwidth *BandwidthUsage     `json:"replicationBandwidth"`
}

// Analytics reads GET /v1/projects/{project}/analytics with a ts-auth project
// access token supplied to Management.WithToken.
func (u *ManagementUser) Analytics(ctx context.Context, project string, opts MetricsOptions) (*AnalyticsResponse, error) {
	var out AnalyticsResponse
	if err := u.metrics(ctx, project, "analytics", opts, &out); err != nil {
		return nil, err
	}
	return validateAnalytics(&out)
}

func validateAnalytics(out *AnalyticsResponse) (*AnalyticsResponse, error) {
	if out.Items == nil || out.Range == "" || out.From.IsZero() || out.To.IsZero() || out.BucketSeconds <= 0 {
		return nil, errors.New("terrabase: metrics response is missing its window or items")
	}
	for _, item := range out.Items {
		if item.DatabaseName == "" || item.Region == "" || item.Series == nil {
			return nil, errors.New("terrabase: analytics response requires database name, region and series")
		}
		for _, point := range item.Series {
			if point.Start.IsZero() || point.Requests == nil || point.Operations == nil || point.Latency == nil || point.ClientBandwidth == nil || point.ReplicationBandwidth == nil {
				return nil, errors.New("terrabase: analytics bucket is missing required observations")
			}
		}
	}
	return out, nil
}

// Replication reads GET /v1/projects/{project}/replication. Database IDs are plain
// ULIDs; an empty filter selects all databases. Use Management.WithToken with
// a ts-auth project access token. No usage is computed by the client.
func (u *ManagementUser) Replication(ctx context.Context, project string, opts MetricsOptions) (*ReplicationResponse, error) {
	var out ReplicationResponse
	if err := u.metrics(ctx, project, "replication", opts, &out); err != nil {
		return nil, err
	}
	return validateReplication(&out)
}

func validateReplication(out *ReplicationResponse) (*ReplicationResponse, error) {
	if out.Items == nil || out.Range == "" || out.From.IsZero() || out.To.IsZero() || out.BucketSeconds <= 0 {
		return nil, errors.New("terrabase: metrics response is missing its window or items")
	}
	for _, item := range out.Items {
		if item.DatabaseName == "" || item.Region == "" || item.Status == "" || item.Bandwidth == nil || item.Series == nil {
			return nil, errors.New("terrabase: replication response is missing replica observations")
		}
		for _, point := range item.Series {
			if point.Start.IsZero() || point.Bandwidth == nil {
				return nil, errors.New("terrabase: replication bucket requires start and bandwidth")
			}
		}
	}
	return out, nil
}

func (u *ManagementUser) metrics(ctx context.Context, project, endpoint string, opts MetricsOptions, out any) error {
	if u.err != nil {
		return u.err
	}
	if u.token == "" {
		return errors.New("terrabase: metrics require WithToken with a ts-auth project access token")
	}
	if err := ValidateID(project); err != nil {
		return fmt.Errorf("terrabase: metrics project ID: %w", err)
	}
	q, err := opts.query()
	if err != nil {
		return err
	}

	_, err = u.c.call(ctx, request{method: http.MethodGet, path: []string{"v1", "projects", project, endpoint}, query: q, header: u.header, token: u.token, replaySafe: true}, nil, out)
	return err
}

func (opts MetricsOptions) query() (url.Values, error) {
	if opts.DatabaseID != "" {
		if err := ValidateID(opts.DatabaseID); err != nil {
			return nil, fmt.Errorf("terrabase: metrics database ID: %w", err)
		}
	}
	switch opts.Range {
	case "", Range1Hour, Range24Hours, Range7Days, Range30Days:
	default:
		return nil, errors.New("terrabase: metrics range must be 1h, 24h, 7d or 30d")
	}
	q := url.Values{}
	for key, value := range map[string]string{"range": string(opts.Range), "database": opts.DatabaseID, "region": opts.Region} {
		if value != "" {
			q.Set(key, value)
		}
	}
	return q, nil
}

// ProjectAnalytics reads project analytics with the client's project token.
func (c *Client) ProjectAnalytics(ctx context.Context, project string, opts MetricsOptions) (*AnalyticsResponse, error) {
	q, err := opts.query()
	if err != nil {
		return nil, err
	}
	out, err := projectCall[AnalyticsResponse](ctx, c, http.MethodGet, project, q, nil, "analytics")
	if err != nil {
		return nil, err
	}
	return validateAnalytics(out)
}

// ProjectReplication reads project replication with the client's project token.
func (c *Client) ProjectReplication(ctx context.Context, project string, opts MetricsOptions) (*ReplicationResponse, error) {
	q, err := opts.query()
	if err != nil {
		return nil, err
	}
	out, err := projectCall[ReplicationResponse](ctx, c, http.MethodGet, project, q, nil, "replication")
	if err != nil {
		return nil, err
	}
	return validateReplication(out)
}

// Analytics reads this named database's analytics; DatabaseID is ignored.
func (d *Database) Analytics(ctx context.Context, opts MetricsOptions) (*AnalyticsResponse, error) {
	opts.DatabaseID = ""
	q, err := opts.query()
	if err != nil {
		return nil, err
	}
	out, err := databaseCall[AnalyticsResponse](ctx, d, http.MethodGet, q, nil, "analytics")
	if err != nil {
		return nil, err
	}
	return validateAnalytics(out)
}

// Replication reads this named database's replication; DatabaseID is ignored.
func (d *Database) Replication(ctx context.Context, opts MetricsOptions) (*ReplicationResponse, error) {
	opts.DatabaseID = ""
	q, err := opts.query()
	if err != nil {
		return nil, err
	}
	out, err := databaseCall[ReplicationResponse](ctx, d, http.MethodGet, q, nil, "replication")
	if err != nil {
		return nil, err
	}
	return validateReplication(out)
}
