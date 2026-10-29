package v1_test

import (
	"fmt"
	"net/http"
	"strings"
	"testing"
	"time"

	tb "terrascale.tech/sdk/v1"
)

func TestPublicBackupContracts(t *testing.T) {
	const at = "2026-10-22T00:00:00Z"
	const backup = `{"id":"` + parityID + `","databaseId":"` + parityID + `","status":"ready","trigger":"manual","sizeBytes":9007199254740993,"createdAt":"` + at + `"}`
	const restored = `{"id":"` + parityID + `","projectId":"` + parityProject + `","name":"restored","branchedFrom":"` + parityID + `","createdAt":"` + at + `","homeRegion":null,"replicaRegions":null,"placementVersion":9007199254740993,"replicas":[]}`
	const policy = `{"fullBackupCron":null,"retentionDays":7,"disabled":false,"updatedAt":null}`
	const window = `{"earliest":"` + at + `","latest":"` + at + `"}`
	for _, endpoint := range []string{"create", "list", "restore", "auto name", "get policy", "set policy", "window", "plan", "restore time"} {
		t.Run(endpoint, func(t *testing.T) {
			method, path, query, body, reply, status := http.MethodGet, "/backups", "", "", `{"items":[`+backup+`],"next":"opaque"}`, http.StatusOK
			switch endpoint {
			case "create":
				method, reply, status = http.MethodPost, backup, http.StatusCreated
			case "list":
				query = "after=opaque%2B%2F%3D&limit=2"
			case "restore", "auto name":
				method, path, body, reply, status = http.MethodPost, "/backups/"+parityID+"/restore", `{"name":"restored"}`, restored, http.StatusCreated
				if endpoint == "auto name" {
					body = `{}`
				}
			case "get policy", "set policy":
				path, reply = "/backup-policy", policy
				if endpoint == "set policy" {
					method, body = http.MethodPut, `{"fullBackupCron":null,"retentionDays":7,"disabled":false}`
				}
			case "window":
				path, query, reply = "/point-in-time", "destination=customer", window
			case "plan":
				method, path, body, reply = http.MethodPost, "/point-in-time/plan", `{"pointInTime":"`+at+`","destination":"customer"}`, `{"window":`+window+`,"pointInTime":"`+at+`","snapshotAt":"`+at+`","latestSegmentAt":null,"segmentCount":0,"historyCheck":"metadata"}`
			case "restore time":
				method, path, body, reply, status = http.MethodPost, "/point-in-time/restore", `{"pointInTime":"`+at+`","destination":"customer","name":"restored"}`, restored, http.StatusCreated
			}
			d := parityClient(t, method, parityDatabase+path, query, body, reply, status).Database(parityProject, "main")
			instant, err := time.Parse(time.RFC3339, at)
			if err != nil {
				t.Fatal(err)
			}
			request := tb.PointInTimeRequest{PointInTime: instant, Destination: "customer", Name: "restored"}
			switch endpoint {
			case "create":
				out, err := d.CreateBackup(t.Context())
				if err != nil || out.ID != parityID || out.SizeBytes != 9007199254740993 {
					t.Fatalf("backup = %+v, %v", out, err)
				}
			case "list":
				out, err := d.Backups(t.Context(), tb.PageOptions{After: "opaque+/=", Limit: 2})
				if err != nil || len(out.Items) != 1 || out.Next != "opaque" {
					t.Fatalf("backups = %+v, %v", out, err)
				}
			case "restore", "auto name", "restore time":
				var out *tb.ManagedDatabase
				if endpoint == "restore time" {
					out, err = d.RestorePointInTime(t.Context(), request)
				} else {
					name := "restored"
					if endpoint == "auto name" {
						name = ""
					}
					out, err = d.RestoreBackup(t.Context(), parityID, name)
				}
				if err != nil || out.ID != parityID || out.ProjectID != parityProject || out.Name != "restored" || out.BranchedFrom == nil || *out.BranchedFrom != parityID || out.CreatedAt.IsZero() || out.Region != "" || out.HomeRegion != nil || out.ReplicaRegions != nil || out.PlacementVersion != 9007199254740993 || out.Replicas == nil || len(out.Replicas) != 0 {
					t.Fatalf("restored = %+v, %v", out, err)
				}
			case "get policy", "set policy":
				var out *tb.BackupPolicy
				disabled := false
				if endpoint == "get policy" {
					out, err = d.BackupPolicy(t.Context())
				} else {
					out, err = d.SetBackupPolicy(t.Context(), tb.BackupPolicyRequest{RetentionDays: 7, Disabled: &disabled})
				}
				if err != nil || out.RetentionDays != 7 || out.FullBackupCron != nil || out.Disabled || out.UpdatedAt != nil {
					t.Fatalf("policy = %+v, %v", out, err)
				}
			case "window":
				out, err := d.RestoreWindow(t.Context(), "customer")
				if err != nil || !out.Latest.Equal(instant) {
					t.Fatalf("window = %+v, %v", out, err)
				}
			case "plan":
				out, err := d.PlanPointInTime(t.Context(), request)
				if err != nil || out.HistoryCheck != "metadata" || out.LatestSegmentAt != nil {
					t.Fatalf("plan = %+v, %v", out, err)
				}
			}
		})
	}
}

func TestPublicBackupDestinationContracts(t *testing.T) {
	const response = `{"endpoint":"https://s3.example.test/","region":"gru","bucket":"test-bucket","mode":"both","updatedAt":"2026-10-22T00:00:00Z"}`
	in := tb.ProjectBackupDestinationRequest{Endpoint: "https://s3.example.test/", Region: "gru", Bucket: "test-bucket", Mode: "both", AccessKeyID: "fake-access-id", SecretKey: "fake-secret-value"}
	for _, format := range []string{"%v", "%+v", "%#v", "%s"} {
		if text := fmt.Sprintf(format, in); strings.Contains(text, in.AccessKeyID) || strings.Contains(text, in.SecretKey) {
			t.Fatal("request formatting exposes credentials")
		}
	}
	for _, method := range []string{http.MethodGet, http.MethodPut, http.MethodDelete} {
		t.Run(method, func(t *testing.T) {
			body, reply, status := "", response, http.StatusOK
			if method == http.MethodPut {
				body = `{"endpoint":"https://s3.example.test/","region":"gru","bucket":"test-bucket","mode":"both","accessKeyId":"fake-access-id","secretKey":"fake-secret-value"}`
			}
			if method == http.MethodDelete {
				reply, status = "", http.StatusNoContent
			}
			c := parityClient(t, method, "/v1/projects/"+parityProject+"/backup-destination", "", body, reply, status)
			var out *tb.ProjectBackupDestination
			var err error
			switch method {
			case http.MethodGet:
				out, err = c.BackupDestination(t.Context(), parityProject)
			case http.MethodPut:
				out, err = c.SetBackupDestination(t.Context(), parityProject, in)
			default:
				if err := c.DeleteBackupDestination(t.Context(), parityProject); err != nil {
					t.Fatal(err)
				}
				return
			}
			if err != nil || out.Mode != "both" || out.Bucket != "test-bucket" || out.UpdatedAt.IsZero() {
				t.Fatal("destination response does not match fixture")
			}
		})
	}
}

func TestPublicRestorePreservesAssignedPlacement(t *testing.T) {
	const response = `{"id":"` + parityID + `","projectId":"` + parityProject + `","name":"restored","branchedFrom":"` + parityID + `","createdAt":"2026-10-22T00:00:00Z","homeRegion":"gru","replicaRegions":["sjc"],"placementVersion":2,"replicas":["sjc"]}`
	c := parityClient(t, http.MethodPost, parityDatabase+"/backups/"+parityID+"/restore", "", `{"name":"restored"}`, response, http.StatusCreated)
	out, err := c.Database(parityProject, "main").RestoreBackup(t.Context(), parityID, "restored")
	if err != nil || out.HomeRegion == nil || *out.HomeRegion != "gru" || len(out.ReplicaRegions) != 1 || out.ReplicaRegions[0] != "sjc" || out.PlacementVersion != 2 || len(out.Replicas) != 1 || out.Replicas[0] != "sjc" || out.Region != "" {
		t.Fatalf("placement = %+v, %v", out, err)
	}
}
