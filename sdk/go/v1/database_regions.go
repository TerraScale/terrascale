package v1

import (
	"context"
	"net/http"
	"time"
)

type DatabaseRegions struct {
	DatabaseID string          `json:"databaseId" ulid:"required"`
	ObservedAt time.Time       `json:"observedAt"`
	Regions    []ReplicaStatus `json:"regions"`
}

type ReplicaStatus struct {
	Region string `json:"region"`
	Role   string `json:"role,omitempty"`
	// LagMs is the age of the oldest known commit missing from the last
	// served revision; zero when caught up, nil without sufficient observations.
	LagMs *float64 `json:"lagMs"`
	// LastPulledAt is when a complete pull was served.
	LastPulledAt *time.Time `json:"lastPulledAt"`
	// LastVerified is nil until checksum verification reports a result.
	LastVerified *time.Time `json:"lastVerified"`
	// Revision is the revision served by the last complete pull.
	Revision *string `json:"revision"`
}

func (d *Database) Regions(ctx context.Context) (*DatabaseRegions, error) {
	return databaseCall[DatabaseRegions](ctx, d, http.MethodGet, nil, nil, "regions")
}
