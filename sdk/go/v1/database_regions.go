package v1

import (
	"context"
	"net/http"
	"time"
)

// DatabaseRegions is one database's placement and the home-observed replica
// pull progress, sampled at ObservedAt. A served pull does not attest that the
// replica applied or verified it; observations reset when the database reopens.
type DatabaseRegions struct {
	DatabaseID string          `json:"databaseId" ulid:"required"`
	HomeRegion string          `json:"homeRegion"`
	ObservedAt time.Time       `json:"observedAt"`
	Replicas   []ReplicaStatus `json:"replicas"`
}

// ReplicaStatus is one replica's pull progress as observed by the home region.
// Unknown observations are nil.
type ReplicaStatus struct {
	Region string `json:"region"`
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

// Regions reads this database's home region and replica pull progress with the
// databases:read scope. Answered by the home region; other regions redirect.
func (d *Database) Regions(ctx context.Context) (*DatabaseRegions, error) {
	return databaseCall[DatabaseRegions](ctx, d, http.MethodGet, nil, nil, "regions")
}
