package sdk

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"
)

func deletionTestOperation(database, operation, state string) DeletionOperation {
	at := time.Date(2026, 9, 7, 12, 0, 0, 0, time.UTC)
	r := DeletionRequest{Environment: "production", TenantID: "tenant-1", ProjectID: "project-1", DatabaseID: database, OperationID: operation}
	stop := DeletionStop{DeletionIdentity: DeletionIdentity{Environment: r.Environment, OrganizationID: r.TenantID, ProjectID: r.ProjectID, DatabaseID: database}, OperationID: operation, Region: "GRU"}
	out := DeletionOperation{Request: r, RequestedAt: at, State: state, Targets: []DeletionTarget{{Stop: stop}}}
	if state != "preparing" {
		out.Cutoff = at.Add(time.Second)
		out.Targets[0].Stop.Cutoff = out.Cutoff
		out.Targets[0].Preparation = &DeletionPreparation{DeletionIdentity: stop.DeletionIdentity, OperationID: operation, Region: stop.Region, Prepared: true, LastAccountedAt: at}
	}
	if state == "edge_pending" || state == "cleanup_pending" {
		out.Targets[0].Receipt = &DeletionReceipt{DeletionStop: out.Targets[0].Stop, AccessRevoked: true, UsageStopped: true}
	}
	if state == "cleanup_pending" {
		out.EdgeReceipt = &DeletionEdgeReceipt{Request: r, Cutoff: out.Cutoff, AccessRevoked: true}
	}
	return out
}

func TestDeletionStatusPreservesAuthoritativePhasesAndReceipts(t *testing.T) {
	for _, state := range []string{"preparing", "stopping", "edge_pending", "cleanup_pending"} {
		t.Run(state, func(t *testing.T) {
			expected := deletionTestOperation("db-1", "delete-1", state)
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				if r.Method != http.MethodGet || r.URL.Path != "/api/v1/databases/db-1/deletions/delete-1" || r.Header.Get("Idempotency-Key") != "" || r.Header.Get("X-TerraScale-Tenant-ID") != "tenant-1" || r.Header.Get("X-TerraScale-Project-ID") != "project-1" {
					t.Errorf("status request = %s %s %v", r.Method, r.URL, r.Header)
				}
				_ = json.NewEncoder(w).Encode(expected)
			}))
			defer server.Close()
			client, err := NewClient(Config{APIBaseURL: server.URL, TenantID: "tenant-1", ProjectID: "project-1"})
			if err != nil {
				t.Fatal(err)
			}
			got, err := client.Databases.GetDeletionStatus(t.Context(), "db-1", "delete-1")
			if err != nil {
				t.Fatal(err)
			}
			if got.State != state || !got.RequestedAt.Equal(expected.RequestedAt) || !got.Cutoff.Equal(expected.Cutoff) || len(got.Targets) != 1 {
				t.Fatalf("operation = %+v", got)
			}
			if state == "edge_pending" || state == "cleanup_pending" {
				if got.Targets[0].Receipt == nil || !got.Targets[0].Receipt.UsageStopped || !got.Targets[0].Receipt.AccessRevoked {
					t.Fatalf("stop receipt lost: %+v", got.Targets[0])
				}
			}
			if state == "cleanup_pending" && (got.EdgeReceipt == nil || !got.EdgeReceipt.AccessRevoked) {
				t.Fatalf("edge receipt lost: %+v", got)
			}
		})
	}
}

func TestDeletionStatusRejectsUnrelatedOrFabricatedProgress(t *testing.T) {
	for _, scenario := range []string{"status202", "database", "operation", "tenant", "project", "complete", "target_scope", "target_operation", "preparation", "receipt", "edge", "missing_receipts", "missing_requested_at"} {
		t.Run(scenario, func(t *testing.T) {
			result := deletionTestOperation("db-1", "delete-1", "cleanup_pending")
			status := http.StatusOK
			switch scenario {
			case "status202":
				status = http.StatusAccepted
			case "database":
				result = deletionTestOperation("db-other", "delete-1", "cleanup_pending")
			case "operation":
				result = deletionTestOperation("db-1", "delete-other", "cleanup_pending")
			case "tenant":
				result.Request.TenantID = "tenant-other"
			case "project":
				result.Request.ProjectID = "project-other"
			case "complete":
				result.State = "complete"
			case "target_scope":
				result.Targets[0].Stop.DatabaseID = "unrelated"
			case "target_operation":
				result.Targets[0].Stop.OperationID = "unrelated"
			case "preparation":
				result.Targets[0].Preparation.Region = "FRA"
			case "receipt":
				result.Targets[0].Receipt.UsageStopped = false
			case "edge":
				result.EdgeReceipt.Request.OperationID = "unrelated"
			case "missing_receipts":
				result.Targets[0].Receipt = nil
			case "missing_requested_at":
				result.RequestedAt = time.Time{}
			}
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				w.WriteHeader(status)
				_ = json.NewEncoder(w).Encode(result)
			}))
			defer server.Close()
			client, err := NewClient(Config{APIBaseURL: server.URL, TenantID: "tenant-1", ProjectID: "project-1"})
			if err != nil {
				t.Fatal(err)
			}
			if result, err := client.Databases.GetDeletionStatus(t.Context(), "db-1", "delete-1"); err == nil {
				t.Fatalf("accepted inconsistent response: %+v", result)
			}
		})
	}
}

func TestDeletionStatusCancellationDoesNotResubmitDeletion(t *testing.T) {
	requests := 0
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { requests++; t.Error("cancelled request reached server") }))
	defer server.Close()
	client, err := NewClient(Config{APIBaseURL: server.URL})
	if err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithCancel(t.Context())
	cancel()
	if _, err := client.Databases.GetDeletionStatus(ctx, "db-1", "delete-1"); !errors.Is(err, context.Canceled) {
		t.Fatalf("cancelled status = %v", err)
	}
	if _, err := client.Databases.GetDeletionStatus(t.Context(), "db-1", ""); err == nil {
		t.Fatal("blank operation accepted")
	}
	if requests != 0 {
		t.Fatalf("requests = %d", requests)
	}
}

func TestDeletionStatusAcceptsOnlyCompleteDescendantTargetGraphs(t *testing.T) {
	for _, scenario := range []string{"grandchild", "branch_root", "missing_parent", "cycle", "missing_root", "conflicting_parent"} {
		t.Run(scenario, func(t *testing.T) {
			result := deletionTestOperation("db-1", "delete-1", "preparing")
			branch := result.Targets[0]
			branch.Stop.DatabaseID, branch.Stop.ParentDatabaseID = "branch-1", "db-1"
			grandchild := branch
			grandchild.Stop.DatabaseID, grandchild.Stop.ParentDatabaseID = "grandchild-1", "branch-1"
			// Reverse order proves discovery does not depend on parent-first sorting.
			result.Targets = append([]DeletionTarget{grandchild, branch}, result.Targets...)
			valid := scenario == "grandchild" || scenario == "branch_root"
			switch scenario {
			case "branch_root":
				result.Targets[2].Stop.ParentDatabaseID = "outside-parent"
			case "missing_parent":
				result.Targets[0].Stop.ParentDatabaseID = "missing"
			case "cycle":
				result.Targets[1].Stop.ParentDatabaseID = "grandchild-1"
			case "missing_root":
				result.Targets = result.Targets[:2]
			case "conflicting_parent":
				otherRegion := grandchild
				otherRegion.Stop.Region, otherRegion.Stop.ParentDatabaseID = "FRA", "db-1"
				result.Targets = append(result.Targets, otherRegion)
			}
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { _ = json.NewEncoder(w).Encode(result) }))
			defer server.Close()
			client, err := NewClient(Config{APIBaseURL: server.URL})
			if err != nil {
				t.Fatal(err)
			}
			got, err := client.Databases.GetDeletionStatus(t.Context(), "db-1", "delete-1")
			if valid && (err != nil || len(got.Targets) != 3) {
				t.Fatalf("valid descendant graph = %+v, err = %v", got, err)
			}
			if !valid && err == nil {
				t.Fatalf("unrelated or cyclic target graph accepted: %+v", got)
			}
		})
	}
}
