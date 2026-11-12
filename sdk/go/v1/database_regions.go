package v1

import (
	"context"
	"encoding/json"
	"net/http"
	"time"
)

// DatabaseRegions is placement and writer-observed pull progress sampled at ObservedAt.
// A served pull does not prove that a remote region applied or verified it.
type DatabaseRegions struct {
	_          struct{}
	DatabaseID string          `json:"databaseId" ulid:"required"`
	ObservedAt time.Time       `json:"observedAt"`
	Regions    []ReplicaStatus `json:"regions"`
	// Deprecated: raw earlier field, passed through for ts -json; absent once the server stops sending it.
	HomeRegion json.RawMessage `json:"homeRegion,omitempty"`
	// Deprecated: raw earlier field, passed through for ts -json; absent once the server stops sending it.
	Replicas json.RawMessage `json:"replicas,omitempty"`
}

// ReplicaStatus is one selected region's writer-observed pull progress; unknown observations are nil.
type ReplicaStatus struct {
	_      struct{}
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

// Regions reads selected regions and writer-observed pull progress with databases:read.
// A region outside the placement answers 421 wrong_region with routing hints.
func (d *Database) Regions(ctx context.Context) (*DatabaseRegions, error) {
	return databaseCall[DatabaseRegions](ctx, d, http.MethodGet, nil, nil, "regions")
}
