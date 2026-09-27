package core

import (
	"errors"

	sdk "terrascale.tech/sdk"
)

// ErrDataPlane marks a live data plane that is not configured or reachable.
var ErrDataPlane = errors.New("live data plane unavailable")

// ProviderError is a data plane request that failed after the dashboard
// accepted it.
type ProviderError struct {
	Action string
	Err    error
}

func (e ProviderError) Error() string { return e.Action + " failed: " + e.Err.Error() }
func (e ProviderError) Unwrap() error { return e.Err }

// DataPlane returns an SDK client for the principal's selected project.
func (d Deps) DataPlane(p Principal) (*sdk.Client, error) {
	if d.SDK == nil {
		return nil, ErrDataPlane
	}
	client, err := d.SDK.ForScope(p.OrganizationID, p.ProjectID)
	if err != nil {
		return nil, ErrDataPlane
	}
	return client, nil
}
