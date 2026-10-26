package v1

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"time"

	"terrascale.tech/sdk/internal/entityid"
)

// RecoverySelection selects historical live records. Omitted IDs selects the
// whole collection. The server limits previews to 16 selections, 100 records
// and 1 MiB of combined historical/current JSON.
type RecoverySelection struct {
	Collection string   `json:"collection"`
	IDs        []string `json:"ids,omitempty"`
}

// RecoveryPreviewRequest selects a point in time without creating a database.
type RecoveryPreviewRequest struct {
	PointInTime time.Time           `json:"pointInTime"`
	Destination string              `json:"destination,omitempty"`
	Selections  []RecoverySelection `json:"selections"`
}

// RecoveryRecord compares historical content with the current live record.
// A tombstone has null Current but a nonnull CurrentVersion. JSON values retain
// their exact numeric text.
type RecoveryRecord struct {
	Collection     string          `json:"collection"`
	ID             string          `json:"id" documentkey:"required"`
	Historical     json.RawMessage `json:"historical"`
	Current        json.RawMessage `json:"current"`
	CurrentVersion *string         `json:"currentVersion"`
}

// RecoveryPreview contains sensitive data and a process-scoped approval token.
// The token expires after ten minutes; a different node requires a new preview.
type RecoveryPreview struct {
	Approval  string           `json:"approval"`
	ExpiresAt time.Time        `json:"expiresAt"`
	Records   []RecoveryRecord `json:"records"`
}

// RecoveryApproval explicitly approves one previewed record, never its JSON.
type RecoveryApproval struct {
	Collection string `json:"collection"`
	ID         string `json:"id"`
}

// RecoveryRequest approves a nonempty subset of a server-bound preview.
type RecoveryRequest struct {
	Approval string             `json:"approval"`
	Records  []RecoveryApproval `json:"records"`
}

// PreviewSelectiveRecovery rebuilds isolated service scratch and compares selected
// records. It does not reserve quota or change the original database.
// Requires databases:write and documents:read on a customer-owned writable source.
func (d *Database) PreviewSelectiveRecovery(ctx context.Context, in RecoveryPreviewRequest) (*RecoveryPreview, error) {
	for _, s := range in.Selections {
		if err := checkName("collection", s.Collection); err != nil {
			return nil, err
		}
		for _, id := range s.IDs {
			if err := entityid.DocumentKey(id); err != nil {
				return nil, err
			}
		}
	}
	return databaseCall[RecoveryPreview](ctx, d, http.MethodPost, nil, in, "point-in-time", "recovery-preview")
}

// RecoverHistoricalRecords applies approved records into this original database.
// Requires databases:write and documents:write. The server rechecks authorization,
// retention, historical hashes and current versions in the accepting region's
// applied state, then writes atomically through document schema and quota checks.
// A stale record refuses the entire apply; unseen concurrent regional writes
// still follow normal conflict rules.
// This mutation is sent once; reconcile an uncertain outcome before retrying.
func (d *Database) RecoverHistoricalRecords(ctx context.Context, in RecoveryRequest) (*TxResult, error) {
	if in.Approval == "" || len(in.Records) == 0 {
		return nil, errors.New("terrabase: recovery requires an approval token and explicit records")
	}
	for _, r := range in.Records {
		if err := checkName("collection", r.Collection); err != nil {
			return nil, err
		}
		if err := entityid.DocumentKey(r.ID); err != nil {
			return nil, err
		}
	}
	return databaseCall[TxResult](ctx, d, http.MethodPost, nil, in, "point-in-time", "recover")
}
