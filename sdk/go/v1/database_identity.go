package v1

import (
	"context"
	"fmt"
	"net/http"
)

// ErrDatabaseIdentityMismatch means the name resolved to a different immutable
// database ID. The server refuses deletion with HTTP 409.
const ErrDatabaseIdentityMismatch ErrorCode = "database_identity_mismatch"

// DeleteIfID deletes this database and its data only if the server resolves its
// name to expectedID, a canonical uppercase ULID. Requires databases:write.
// A same-name replacement fails with ErrDatabaseIdentityMismatch. This does not
// make a subsequent create or branch operation atomic with deletion.
// Servers without the identity-conditioned route return 404; no unconditional
// deletion is attempted.
func (d *Database) DeleteIfID(ctx context.Context, expectedID string) error {
	if err := checkID(expectedID); err != nil {
		return fmt.Errorf("terrabase: expected database ID: %w", err)
	}
	p, err := d.path("if-id", expectedID)
	if err != nil {
		return err
	}
	_, err = d.c.call(ctx, request{
		method: http.MethodDelete,
		path:   p,
	}, nil, nil)
	return err
}
