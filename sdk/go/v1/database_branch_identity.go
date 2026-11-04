package v1

import (
	"context"
	"errors"
	"fmt"
	"net/http"
)

// ErrDatabaseIdentityChanged means the source name resolved to a different
// immutable ID. The server refuses the branch with HTTP 409 before copying.
const ErrDatabaseIdentityChanged ErrorCode = "database_identity_changed"

// BranchIfID copies this database into name only if the server resolves the
// source to expectedID, a canonical uppercase ULID. Requires databases:write.
// The admitted source ID is retained during copying; concurrent deletion can
// still interrupt the copy. An unsupported server returns 404. No unguarded
// branch is attempted, and no branch lineage or ownership policy is inferred.
func (d *Database) BranchIfID(ctx context.Context, name, expectedID string) (*ManagedDatabase, error) {
	return d.BranchIfIDWithOptions(ctx, name, expectedID, ForkDatabaseOptions{})
}

// BranchIfIDWithOptions adds fork TTL options to the server-enforced source guard.
func (d *Database) BranchIfIDWithOptions(ctx context.Context, name, expectedID string, opts ForkDatabaseOptions) (*ManagedDatabase, error) {
	if opts.PointInTime != nil {
		return nil, errors.New("terrabase: guarded branching does not accept PointInTime")
	}
	if err := checkAgentTTL(opts.TTLSeconds, 31536000); err != nil {
		return nil, err
	}
	if err := checkID(expectedID); err != nil {
		return nil, fmt.Errorf("terrabase: expected source database ID: %w", err)
	}
	if err := checkName("database", name); err != nil {
		return nil, err
	}
	if name == d.name {
		return nil, errors.New("terrabase: branch source and target names must differ")
	}
	body := struct {
		Name string `json:"name"`
		ForkDatabaseOptions
	}{name, opts}
	return databaseCall[ManagedDatabase](ctx, d, http.MethodPost, nil, body, "branches", "if-id", expectedID)
}
