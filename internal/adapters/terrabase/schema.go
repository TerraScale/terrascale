package terrabase

import (
	"context"
	"errors"

	v1 "terrascale.tech/sdk/v1"
)

var stateCollections = []string{
	"users", "access_tokens", "sessions", "device_grants", "login_throttle",
	"organizations", "memberships", "invitations", "projects", "databases",
	"api_keys", "billing", "checkout_sessions", "plans", "notifications",
	"audit_events", "admin_actions", "stripe_customers", "stripe_organizations",
}

// EnsureCollections explicitly provisions the already-selected state database.
// Startup calls it; health checks only observe state. The current Documents API
// has no native constraints, indexes or TTL to provision.
func EnsureCollections(ctx context.Context, db *v1.Database) error {
	for _, name := range stateCollections {
		if _, err := db.Collection(name).Info(ctx); err == nil {
			continue
		} else if !errors.Is(err, v1.ErrUnknownCollection) {
			return documentError(err)
		}
		if _, err := db.CreateCollection(ctx, name); err != nil && !errors.Is(err, v1.ErrNameTaken) {
			return documentError(err)
		}
	}
	return nil
}
