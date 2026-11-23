package v1

import (
	"context"
	"net/http"
	"time"
)

type DatabaseRegions struct {
	_          struct{}
	DatabaseID string          `json:"databaseId" ulid:"required"`
	ObservedAt time.Time       `json:"observedAtUtc"`
	Regions    []ReplicaStatus `json:"regions"`
}

type ReplicaStatus struct {
	_               struct{}
	Region          string   `json:"region"`
	Role            string   `json:"role,omitempty"`
	AppliedBookmark string   `json:"appliedBookmark"`
	LagMs           *float64 `json:"lagMs"`
	Healthy         bool     `json:"healthy"`
}

func (d *Database) Regions(ctx context.Context) (*DatabaseRegions, error) {
	return databaseCall[DatabaseRegions](ctx, d, http.MethodGet, nil, nil, "regions")
}
