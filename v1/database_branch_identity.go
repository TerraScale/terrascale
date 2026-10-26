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
	if err := checkID(expectedID); err != nil {
		return nil, fmt.Errorf("terrabase: expected source database ID: %w", err)
	}
	if err := checkName("database", name); err != nil {
		return nil, err
	}
	if name == d.name {
		return nil, errors.New("terrabase: branch source and target names must differ")
	}
	return databaseCall[ManagedDatabase](ctx, d, http.MethodPost, nil, nameBody{name}, "branches", "if-id", expectedID)
}
