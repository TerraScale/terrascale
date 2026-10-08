package core

import (
	"context"
	"time"
)

// MetricsReader is the read-only TS-762 ts-go boundary. TerraBase owns
// analytics and replication.
// The SDK adapter is wired when those typed methods are published.
type MetricsReader interface {
	Analytics(context.Context, string, MetricsScope) (Analytics, error)
	Replication(context.Context, string, MetricsScope) (Replication, error)
}

// RegionHealthReader reads public ts-status observations through ts-go,
// without forwarding the user's TerraBase token to a different service.
type RegionHealthReader interface {
	RegionHealth(context.Context, []string) ([]RegionHealth, error)
}

// MetricsScope carries provider IDs unchanged. Bucketing belongs to TerraBase.
type MetricsScope struct {
	OrganizationID, ProjectID, DatabaseID, Region, Range string
}

func MetricsRange(value string) string {
	switch value {
	case "1h", "24h", "7d", "30d":
		return value
	default:
		return "24h"
	}
}

// MetricsSelection authorizes the complete scope before any SDK call. IDs
// are never synthesized or translated into a different identifier format.
func (d Deps) MetricsSelection(ctx context.Context, p Principal, projectID, databaseID, region, rangeKey string) (Project, MetricsScope, error) {
	a, err := d.Store.SnapshotAs(ctx, p)
	if err != nil {
		return Project{}, MetricsScope{}, err
	}
	if projectID == "" {
		projectID = p.ProjectID
	}
	project, err := a.ProjectFor(p, p.OrganizationID, projectID)
	if err != nil {
		return Project{}, MetricsScope{}, err
	}
	scope := MetricsScope{OrganizationID: p.OrganizationID, ProjectID: project.ID, DatabaseID: databaseID, Region: region, Range: MetricsRange(rangeKey)}
	if databaseID != "" {
		db, ok := project.Databases[databaseID]
		if !ok {
			return Project{}, MetricsScope{}, ErrNotFound
		}
		if db.TerraBaseID != "" {
			scope.DatabaseID = db.TerraBaseID
		}
	}
	if region != "" {
		found := false
		for _, db := range project.Databases {
			if databaseID != "" && db.ID != databaseID {
				continue
			}
			if db.Region == region {
				found = true
			}
			for _, code := range db.Regions {
				if code == region {
					found = true
				}
			}
		}
		if !found {
			return Project{}, MetricsScope{}, Invalid("select a region used by this database or project")
		}
	}
	return project, scope, nil
}

type Analytics struct {
	Series []AnalyticsPoint
}

// Counts are per bucket; gauges and percentiles are supplied by TerraBase.
// Latency is nullable milliseconds, preserving missing observations and zero.
type AnalyticsPoint struct {
	DatabaseID, Region                                                   string
	Start                                                                time.Time
	Requests, Operations                                                 map[string]int64
	Errors                                                               int64
	P50LatencyMS, P95LatencyMS, P99LatencyMS                             *float64
	StorageBytes, ClientSentBytes, ClientReceivedBytes, ReplicationBytes int64
}

type Replication struct {
	Regions []ReplicationRegion
	Pairs   []ReplicationPair
}

type ReplicationRegion struct {
	DatabaseID, Region, Status string
	ReplicatedOps              int64
	LagMS, ConvergenceMS       *float64
	Divergence                 *int64
	LastApplied                *time.Time
}

// Each ordered pair is one direction; sent and received describe the same
// transfer at its endpoints. Never add sent+received as billable bandwidth.
type ReplicationPair struct {
	DatabaseID, FromRegion, ToRegion string
	SentBytes, ReceivedBytes         int64
	Series                           []ReplicationPoint
}

type ReplicationPoint struct {
	Start                    time.Time
	SentBytes, ReceivedBytes int64
}

type RegionHealth struct {
	Region, Status string
	LatencyMS      *float64
}
