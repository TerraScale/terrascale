package sdk

import (
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"net/url"
	"strings"
	"time"
)

// DeletionRequest identifies the durable deletion admitted by the authority.
type DeletionRequest struct {
	Environment string `json:"environment"`
	TenantID    string `json:"tenant_id"`
	ProjectID   string `json:"project_id"`
	DatabaseID  string `json:"database_id"`
	OperationID string `json:"operation_id"`
}

// DeletionIdentity scopes a database or one of its descendant branches.
type DeletionIdentity struct {
	Environment      string `json:"environment"`
	OrganizationID   string `json:"organization_id"`
	ProjectID        string `json:"project_id"`
	DatabaseID       string `json:"database_id"`
	ParentDatabaseID string `json:"parent_database_id,omitempty"`
}

// DeletionStop is the authority's exact regional stop request.
type DeletionStop struct {
	DeletionIdentity
	OperationID string    `json:"operation_id"`
	Region      string    `json:"region"`
	Cutoff      time.Time `json:"cutoff"`
	RetainUntil time.Time `json:"retain_until,omitzero"`
}

// DeletionPreparation reports a member's last accounted usage boundary.
type DeletionPreparation struct {
	DeletionIdentity
	OperationID     string    `json:"operation_id"`
	Region          string    `json:"region"`
	Prepared        bool      `json:"prepared"`
	LastAccountedAt time.Time `json:"last_accounted_at"`
}

// DeletionReceipt reports completed access revocation and usage stopping.
type DeletionReceipt struct {
	DeletionStop
	AccessRevoked bool `json:"access_revoked"`
	UsageStopped  bool `json:"usage_stopped"`
}

// DeletionEdgeReceipt reports edge access revocation for this exact operation.
type DeletionEdgeReceipt struct {
	Request       DeletionRequest `json:"request"`
	Cutoff        time.Time       `json:"cutoff"`
	AccessRevoked bool            `json:"access_revoked"`
}

// DeletionTarget preserves preparation and stop receipts without inventing progress.
type DeletionTarget struct {
	Preparation *DeletionPreparation `json:"preparation,omitempty"`
	Stop        DeletionStop         `json:"stop"`
	Receipt     *DeletionReceipt     `json:"receipt,omitempty"`
}

// DeletionOperation is authoritative deletion progress. Its states are preparing,
// stopping, edge_pending, and cleanup_pending. Cleanup pending does not mean
// physical deletion has completed.
type DeletionOperation struct {
	RequestedAt time.Time            `json:"requested_at"`
	EdgeReceipt *DeletionEdgeReceipt `json:"edge_receipt,omitempty"`
	Request     DeletionRequest      `json:"request"`
	State       string               `json:"state"`
	Cutoff      time.Time            `json:"cutoff"`
	Targets     []DeletionTarget     `json:"targets"`
}

type deletionResponse struct {
	DeletionOperation
	status int
}

func (o *deletionResponse) decodeHTTPResponse(status int, body []byte) error {
	if status != o.status {
		return fmt.Errorf("sdk: expected deletion status %d, got %d", o.status, status)
	}
	var result DeletionOperation
	if err := json.Unmarshal(body, &result); err != nil {
		return err
	}
	if err := result.validate(); err != nil {
		return err
	}
	o.DeletionOperation = result
	return nil
}

func (o *DeletionOperation) validate() error {
	invalid := func() error {
		return fmt.Errorf("sdk: deletion response has inconsistent durable identity or receipts")
	}
	r := o.Request
	if r.Environment == "" || r.TenantID == "" || r.ProjectID == "" || r.DatabaseID == "" || r.OperationID == "" || o.RequestedAt.IsZero() || len(o.Targets) == 0 || len(o.Targets) > 3000 {
		return invalid()
	}
	switch o.State {
	case "preparing", "stopping", "edge_pending", "cleanup_pending":
	default:
		return invalid()
	}
	if o.Cutoff.IsZero() != (o.State == "preparing") {
		return invalid()
	}
	seen := make(map[string]bool)
	parents := make(map[string]string)
	allPrepared, allStopped := true, true
	for _, target := range o.Targets {
		stop := target.Stop
		key := stop.DatabaseID + "/" + stop.Region
		if stop.Environment != r.Environment || stop.OrganizationID != r.TenantID || stop.ProjectID != r.ProjectID || stop.DatabaseID == "" || stop.OperationID != r.OperationID || !stop.Cutoff.Equal(o.Cutoff) || (stop.Region != "GRU" && stop.Region != "SFO" && stop.Region != "FRA") || seen[key] {
			return invalid()
		}
		seen[key] = true
		if parent, exists := parents[stop.DatabaseID]; exists && parent != stop.ParentDatabaseID {
			return invalid()
		}
		parents[stop.DatabaseID] = stop.ParentDatabaseID
		if preparation := target.Preparation; preparation != nil {
			if preparation.DeletionIdentity != stop.DeletionIdentity || preparation.OperationID != stop.OperationID || preparation.Region != stop.Region || !preparation.Prepared || preparation.LastAccountedAt.IsZero() {
				return invalid()
			}
		} else {
			allPrepared = false
		}
		if receipt := target.Receipt; receipt != nil {
			if receipt.DeletionIdentity != stop.DeletionIdentity || receipt.OperationID != stop.OperationID || receipt.Region != stop.Region || !receipt.Cutoff.Equal(stop.Cutoff) || !receipt.RetainUntil.Equal(stop.RetainUntil) || !receipt.AccessRevoked || !receipt.UsageStopped {
				return invalid()
			}
		} else {
			allStopped = false
		}
	}
	// The authority includes every descendant branch, each naming its immediate
	// parent. All target paths must reach the requested database; deleting a
	// branch itself may legitimately leave that root's parent outside this set.
	if _, present := parents[r.DatabaseID]; !present {
		return invalid()
	}
	resolved := map[string]bool{r.DatabaseID: true}
	for database := range parents {
		var path []string
		visiting := make(map[string]bool)
		for current := database; !resolved[current]; {
			parent, exists := parents[current]
			if !exists || visiting[current] {
				return invalid()
			}
			visiting[current] = true
			path = append(path, current)
			current = parent
		}
		for _, database := range path {
			resolved[database] = true
		}
	}
	if allPrepared != (o.State != "preparing") || allStopped != (o.State == "edge_pending" || o.State == "cleanup_pending") || (o.EdgeReceipt != nil) != (o.State == "cleanup_pending") {
		return invalid()
	}
	if edge := o.EdgeReceipt; edge != nil && (edge.Request != r || !edge.Cutoff.Equal(o.Cutoff) || !edge.AccessRevoked) {
		return invalid()
	}
	return nil
}

func (d *DatabasesClient) validateDeletionScope(operation *DeletionOperation, database, operationID string) error {
	r := operation.Request
	if r.DatabaseID != database || r.OperationID != operationID || (d.c.cfg.TenantID != "" && r.TenantID != d.c.cfg.TenantID) || (d.c.cfg.ProjectID != "" && r.ProjectID != d.c.cfg.ProjectID) {
		return fmt.Errorf("sdk: deletion response does not match the requested scope and operation")
	}
	return nil
}

// GetDeletionStatus reads the existing deletion operation without resubmitting it.
// It remains useful after database access has stopped. Canceling ctx cancels the
// status request; it does not cancel the durable deletion.
func (d *DatabasesClient) GetDeletionStatus(ctx context.Context, database, operationID string, opts ...RequestOption) (*DeletionOperation, error) {
	if err := managementContext(ctx); err != nil {
		return nil, err
	}
	path, err := databasePath(database)
	if err != nil {
		return nil, err
	}
	if strings.TrimSpace(operationID) == "" || len(operationID) > 128 {
		return nil, &ValidationError{Field: "operation_id", Message: "must be nonblank and at most 128 bytes"}
	}
	out := deletionResponse{status: http.StatusOK}
	if err := d.c.do(ctx, http.MethodGet, path+"/deletions/"+url.PathEscape(operationID), nil, nil, &out, "deletion_status", opts...); err != nil {
		return nil, err
	}
	if err := d.validateDeletionScope(&out.DeletionOperation, database, operationID); err != nil {
		return nil, err
	}
	return &out.DeletionOperation, nil
}
