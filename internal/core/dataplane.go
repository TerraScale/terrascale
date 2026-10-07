package core

import (
	"errors"

	"terrascale.tech/sdk/v1"
)

// ErrDataPlane marks a live data plane that is not configured or reachable.
var ErrDataPlane = errors.New("live data plane unavailable")

// ProviderError is a data plane request that failed after the dashboard
// accepted it.
type ProviderError struct {
	Action string
	Err    error
}

func (d Deps) Documents(p Principal, database string) (*v1.Database, error) {
	if d.SDK == nil {
		return nil, ErrDataPlane
	}
	client, err := d.SDK.ForDocuments(p.OrganizationID, p.ProjectID)
	if err != nil {
		return nil, ErrDataPlane
	}
	return client.Database(p.ProjectID, database), nil
}

func (e ProviderError) Error() string { return e.Action + " failed: " + e.Err.Error() }
func (e ProviderError) Unwrap() error { return e.Err }
