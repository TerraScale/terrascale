package sdk

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"math"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

func TestNewProducerManagementRoutes(t *testing.T) {
	const usage = `{"tenant_id":"tenant-1","database_id":"db-1","snapshot_sequence":18446744073709551615,"meters":[{"meter":"document_write_units","units":"170141183460469231731687303715884105727"},{"meter":"storage_byte_seconds","units":"9007199254740993"}],"reads":{"total_reads":18446744073709551615}}`
	const schema = `{"target":{"database":"demo","branch":"main","collection":"orders"},"identity":{"database_id":"db-1","placement":["GRU"]},"schema":{"name":"orders","fields":[],"historyRetentionDays":7,"mergePolicy":"last-write-wins","version":18446744073709551615,"renames":[{"from":"old","to":"new"}]},"generation":18446744073709551615,"schema_version":18446744073709551615,"migration":{"operation_id":"migration-1","state":"running","steps":[{"kind":"field_rename","from":"old","to":"new"}],"total":18446744073709551615,"completed":9007199254740993,"next_cursor":"opaque","message":"Backfilling"}}`
	const recommendation = `{"generation":18446744073709551615,"truncated":true,"recommendations":[{"candidate_id":"candidate-1","collection":"orders","fields":["status"],"score":1.25,"maintenance_cost":18446744073709551615,"generation":18446744073709551615,"needs_descriptor":true}]}`
	const endpoint = `{"id":"hook-1","endpoint":"https://hooks.example.test/events","created_at":18446744073709551615,"deleted":false}`
	const delivery = `{"id":"delivery-1","event_id":"event-1","revision":18446744073709551615,"retry_started_at":18446744073709551615,"attempts":4294967295,"next_attempt_at":18446744073709551615,"status":503,"delivered":false,"terminal":false}`
	const exportStatus = `{"job_id":"job-1","status":"Running","records":18446744073709551615,"estimated_records":null,"progress_basis_points":25,"resume_cursor":"opaque","manifest":{"path":"manifest.json","length":18446744073709551615,"checksum":"digest"},"error":null}`
	tests := []struct {
		name, method, path, body, response string
		status                             int
		call                               func(*Client) (any, error)
		check                              func(*testing.T, any)
	}{
		{name: "usage", method: "GET", path: "/api/v1/databases/demo/usage", response: usage, call: func(c *Client) (any, error) { return c.Usage.GetDatabaseUsage(t.Context(), "demo") }, check: func(t *testing.T, v any) {
			u := v.(*DatabaseUsage)
			if u.SnapshotSequence != math.MaxUint64 || u.Meters[0].Units != "170141183460469231731687303715884105727" || u.Reads.TotalReads != math.MaxUint64 {
				t.Fatalf("usage lost facts: %+v", u)
			}
		}},
		{name: "schema-get", method: "GET", path: "/api/v1/databases/demo/collections/orders/schema", response: schema, call: func(c *Client) (any, error) { return c.Databases.GetCollectionSchema(t.Context(), "demo", "orders") }, check: func(t *testing.T, v any) {
			s := v.(*SchemaDocument)
			if s.Generation != math.MaxUint64 || s.Schema.Version == nil || *s.Schema.Version != math.MaxUint64 || len(s.Schema.Renames) != 1 || s.Migration.Completed != 9007199254740993 {
				t.Fatalf("schema lost progress: %+v", s)
			}
		}},
		{name: "schema-put", method: "PUT", path: "/api/v1/databases/demo/collections/orders/schema", body: `{"name":"orders","fields":[],"historyRetentionDays":7,"mergePolicy":"last-write-wins"}`, response: schema, call: func(c *Client) (any, error) {
			return c.Databases.PutCollectionSchema(t.Context(), "demo", "orders", CollectionSchema{Name: "orders", Fields: []FieldDef{}, HistoryRetentionDays: 7, MergePolicy: MergeLastWriteWins}, WithIdempotencyKey("schema-apply"), WithSchemaGeneration(math.MaxUint64))
		}},
		{name: "recommendations", method: "GET", path: "/api/v1/databases/demo/collections/orders/indexes/recommendations?limit=64", response: recommendation, call: func(c *Client) (any, error) {
			return c.Databases.IndexRecommendations(t.Context(), "demo", "orders", 64)
		}, check: func(t *testing.T, v any) {
			r := v.(*IndexRecommendations)
			if r.Generation != math.MaxUint64 || !r.Truncated || r.Recommendations[0].MaintenanceCost != math.MaxUint64 {
				t.Fatalf("recommendations lost metadata: %+v", r)
			}
		}},
		{name: "index-delete", method: "DELETE", path: "/api/v1/databases/demo/collections/orders/indexes/status", status: 204, call: func(c *Client) (any, error) {
			return nil, c.Databases.DeleteIndex(t.Context(), "demo", "orders", "status")
		}},
		{name: "export-create", method: "POST", path: "/api/v1/databases/demo/exports", body: `{"idempotency_key":"export-1"}`, status: 202, response: `{"job_id":"job-1","status_path":"/api/v1/databases/demo/exports/job-1","resume_cursor":"opaque"}`, call: func(c *Client) (any, error) { return c.Databases.CreateExport(t.Context(), "demo", "export-1") }},
		{name: "export-get", method: "GET", path: "/api/v1/databases/demo/exports/job-1", response: exportStatus, call: func(c *Client) (any, error) { return c.Databases.GetExport(t.Context(), "demo", "job-1") }, check: func(t *testing.T, v any) {
			s := v.(*ExportStatus)
			if s.Status != ExportRunning || s.Records != math.MaxUint64 || s.EstimatedRecords != nil || s.Manifest.Length != math.MaxUint64 {
				t.Fatalf("export lost metadata: %+v", s)
			}
		}},
		{name: "export-resume", method: "POST", path: "/api/v1/databases/demo/exports/job-1/resume", body: `{"resume_cursor":"opaque"}`, response: exportStatus, call: func(c *Client) (any, error) { return c.Databases.ResumeExport(t.Context(), "demo", "job-1", "opaque") }},
		{name: "webhooks-list", method: "GET", path: "/api/v1/databases/demo/webhooks", response: `{"webhooks":[` + endpoint + `]}`, call: func(c *Client) (any, error) { return c.Databases.ListWebhooks(t.Context(), "demo") }},
		{name: "webhooks-create", method: "POST", path: "/api/v1/databases/demo/webhooks", body: `{"endpoint":"https://hooks.example.test/events"}`, status: 201, response: `{"id":"hook-1","endpoint":"https://hooks.example.test/events","created_at":1,"deleted":false,"secret":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}`, call: func(c *Client) (any, error) {
			return c.Databases.CreateWebhook(t.Context(), "demo", "https://hooks.example.test/events")
		}},
		{name: "webhooks-delete", method: "DELETE", path: "/api/v1/databases/demo/webhooks/hook-1", response: endpoint, call: func(c *Client) (any, error) { return c.Databases.DeleteWebhook(t.Context(), "demo", "hook-1") }},
		{name: "deliveries-list", method: "GET", path: "/api/v1/databases/demo/webhooks/hook-1/deliveries", response: `{"deliveries":[` + delivery + `]}`, call: func(c *Client) (any, error) { return c.Databases.ListWebhookDeliveries(t.Context(), "demo", "hook-1") }},
		{name: "delivery-replay", method: "POST", path: "/api/v1/databases/demo/webhooks/hook-1/deliveries/delivery-1/replay", response: delivery, call: func(c *Client) (any, error) {
			return c.Databases.ReplayWebhookDelivery(t.Context(), "demo", "hook-1", "delivery-1")
		}, check: func(t *testing.T, v any) {
			d := v.(*WebhookDelivery)
			if d.Revision != math.MaxUint64 || d.Attempts != math.MaxUint32 || d.Status == nil || *d.Status != 503 {
				t.Fatalf("delivery lost metadata: %+v", d)
			}
		}},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			calls := 0
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				calls++
				if r.Method != test.method || r.URL.RequestURI() != test.path {
					t.Errorf("request = %s %s", r.Method, r.URL)
				}
				body, err := io.ReadAll(r.Body)
				if err != nil {
					t.Error(err)
				}
				if test.body != "" {
					assertProducerJSON(t, body, []byte(test.body))
				} else if len(body) != 0 {
					t.Errorf("unexpected request body: %s", body)
				}
				if test.name == "schema-put" && (r.Header.Get("If-Match") != `"18446744073709551615"` || r.Header.Get("Idempotency-Key") != "schema-apply") {
					t.Error("schema concurrency headers missing")
				}
				if test.name == "export-create" && r.Header.Get("Idempotency-Key") != "export-1" {
					t.Error("export identity mismatch")
				}
				w.Header().Set("Content-Type", "application/json")
				status := test.status
				if status == 0 {
					status = 200
				}
				w.WriteHeader(status)
				_, _ = w.Write([]byte(test.response))
			}))
			defer server.Close()
			client, err := NewClient(Config{APIBaseURL: server.URL})
			if err != nil {
				t.Fatal(err)
			}
			out, err := test.call(client)
			if err != nil {
				t.Fatal(err)
			}
			if calls != 1 {
				t.Fatalf("calls=%d", calls)
			}
			if test.check != nil {
				test.check(t, out)
			}
		})
	}
}

func TestNewManagementRoutesValidateBeforeHTTP(t *testing.T) {
	client, err := NewClient(Config{APIBaseURL: "http://127.0.0.1:1"})
	if err != nil {
		t.Fatal(err)
	}
	if _, err := client.Databases.IndexRecommendations(t.Context(), "demo", "orders", 65); err == nil {
		t.Fatal("unbounded recommendations accepted")
	}
	if _, err := client.Databases.CreateExport(t.Context(), "demo", ""); err == nil {
		t.Fatal("export without retry identity accepted")
	}
	if _, err := client.Databases.ResumeExport(t.Context(), "demo", "job-1", ""); err == nil {
		t.Fatal("export without cursor accepted")
	}
	if _, err := client.Databases.CreateWebhook(t.Context(), "demo", "http://hooks.example.test"); err == nil {
		t.Fatal("plaintext webhook accepted")
	}
	if _, err := client.Databases.PutCollectionSchema(t.Context(), "demo", "orders", CollectionSchema{Name: "orders"}); err == nil {
		t.Fatal("schema without idempotency accepted")
	}
	ctx, cancel := context.WithCancel(t.Context())
	cancel()
	if _, err := client.Databases.CreateExport(ctx, "demo", "export-1"); !errors.Is(err, context.Canceled) {
		t.Fatalf("cancellation = %v", err)
	}
	var document SchemaDocument
	if err := json.Unmarshal([]byte(`{"generation":18446744073709551616}`), &document); err == nil {
		t.Fatal("overflowing schema generation accepted")
	}
}

func TestDownloadExportPreservesBinaryArtifact(t *testing.T) {
	for _, body := range [][]byte{{0, 255, 10, 13, 32}, []byte(" \n\t"), []byte{}} {
		server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			if r.URL.EscapedPath() != "/api/v1/databases/demo/exports/job-1/artifacts/documents%2Fpart-1.jsonl" || r.Header.Get("Accept") != "application/octet-stream" {
				t.Errorf("download request: %s %v", r.URL, r.Header)
			}
			w.Header().Set("Content-Type", "application/octet-stream")
			w.Header().Set("Cache-Control", "private, no-store")
			w.Header().Set("X-TerraScale-Artifact-Length", fmt.Sprint(len(body)))
			w.Header().Set("X-TerraScale-Artifact-Checksum", strings.Repeat("a", 64))
			_, _ = w.Write(body)
		}))
		client, err := NewClient(Config{APIBaseURL: server.URL})
		if err != nil {
			t.Fatal(err)
		}
		got, err := client.Databases.DownloadExport(t.Context(), "demo", "job-1", "documents/part-1.jsonl")
		server.Close()
		if err != nil || got == nil || !bytes.Equal(got.Bytes, body) {
			t.Fatalf("download changed bytes: %x want %x: %v", got, body, err)
		}
	}
}

func TestCreateDatabasePreservesAcceptedProvisioning(t *testing.T) {
	for _, invalid := range []bool{false, true} {
		t.Run(fmt.Sprint(invalid), func(t *testing.T) {
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				var body map[string]any
				if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
					t.Error(err)
				}
				if body["operation_id"] != "operation-1" || body["residency"] != "" {
					t.Errorf("provisioning identity: %+v", body)
				}
				w.Header().Set("Content-Type", "application/json")
				w.WriteHeader(202)
				if invalid {
					_, _ = w.Write([]byte(`{"name":"demo"}`))
					return
				}
				_, _ = w.Write([]byte(`{"environment":"production","tenant_id":"tenant-1","project_id":"project-1","operation_id":"operation-1","database_id":"01ARZ3NDEKTSV4RRFFQ69G5FAV","parent_database_id":null,"key_version":"01ARZ3NDEKTSV4RRFFQ69G5FAW","name":"demo","regions":["GRU"],"residency":"","state":"pending"}`))
			}))
			defer server.Close()
			client, err := NewClient(Config{APIBaseURL: server.URL})
			if err != nil {
				t.Fatal(err)
			}
			result, err := client.Databases.CreateDatabase(t.Context(), CreateDatabaseRequest{Name: "demo", Placement: []RegionCode{RegionGRU}, OperationID: "operation-1"})
			if invalid {
				if err == nil {
					t.Fatal("202 decoded as a ready database")
				}
				return
			}
			if err != nil {
				t.Fatal(err)
			}
			if result.Status != 202 || result.Database != nil || result.Operation == nil || result.Operation.State != "pending" {
				t.Fatalf("accepted work reported as ready: %+v", result)
			}
		})
	}
}

func TestWebhookSecretRotationIsBoundedAndNeverImplicitlyRetried(t *testing.T) {
	var attempts int
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		attempts++
		if r.URL.Path != "/api/v1/databases/demo/webhooks/hook-1/rotate-secret" || r.Method != "POST" {
			t.Errorf("rotation route: %s %s", r.Method, r.URL)
		}
		body, _ := io.ReadAll(r.Body)
		assertProducerJSON(t, body, []byte(`{"overlap_seconds":0}`))
		if r.Header.Get("Idempotency-Key") != "" {
			t.Error("rotation invented an idempotency identity")
		}
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(503)
		_, _ = w.Write([]byte(`{"error":{"code":"unavailable","message":"retry explicitly","fix":"check endpoint state"}}`))
	}))
	defer server.Close()
	client, err := NewClient(Config{APIBaseURL: server.URL, MaxRetryAttempts: 3})
	if err != nil {
		t.Fatal(err)
	}
	zero := uint32(0)
	if _, err := client.Databases.RotateWebhookSecret(t.Context(), "demo", "hook-1", RotateWebhookSecretRequest{OverlapSeconds: &zero}); err == nil {
		t.Fatal("rotation failure hidden")
	}
	if attempts != 1 {
		t.Fatalf("rotation repeated %d times", attempts)
	}
	if _, err := client.Databases.RotateWebhookSecret(t.Context(), "demo", "hook-1", RotateWebhookSecretRequest{OverlapSeconds: &zero}, WithIdempotencyKey("unsupported-replay")); err == nil {
		t.Fatal("rotation failure hidden")
	}
	if attempts != 2 {
		t.Fatalf("rotation with a caller key repeated %d times", attempts)
	}

	tooLong := uint32(3601)
	if _, err := client.Databases.RotateWebhookSecret(t.Context(), "demo", "hook-1", RotateWebhookSecretRequest{OverlapSeconds: &tooLong}); err == nil {
		t.Fatal("unbounded overlap accepted")
	}
	if attempts != 2 {
		t.Fatal("invalid rotation reached HTTP")
	}
}

func TestCloudDevelopmentOperationsUseBoundedPublicContracts(t *testing.T) {
	calls := 0
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		calls++
		w.Header().Set("Content-Type", "application/json")
		switch r.Method + " " + r.URL.RequestURI() {
		case "POST /api/v1/databases/dev/dev/faults":
			body, _ := io.ReadAll(r.Body)
			assertProducerJSON(t, body, []byte(`{"confirmBranch":"dev","fault":"latency","durationMs":30000,"maxOperations":100,"delayMs":1000}`))
			_, _ = w.Write([]byte(`{"active":true,"expiresAtMillis":18446744073709551615}`))
		case "DELETE /api/v1/databases/dev/dev/faults":
			_, _ = w.Write([]byte(`{"active":false,"expiresAtMillis":0}`))
		case "GET /api/v1/databases/dev/dev/logs?limit=100":
			_, _ = w.Write([]byte(`{"entries":[{"sequence":18446744073709551615,"action":"index_created","atMillis":18446744073709551615}],"truncated":true}`))
		default:
			t.Errorf("unknown development route %s %s", r.Method, r.URL)
			w.WriteHeader(404)
		}
	}))
	defer server.Close()
	client, err := NewClient(Config{APIBaseURL: server.URL})
	if err != nil {
		t.Fatal(err)
	}
	request := DevelopmentFaultRequest{ConfirmBranch: "dev", Fault: DevelopmentLatency, DurationMS: 30000, MaxOperations: 100, DelayMS: 1000}
	fault, err := client.Databases.EnableDevelopmentFault(t.Context(), "dev", request)
	if err != nil || !fault.Active || fault.ExpiresAtMillis != math.MaxUint64 {
		t.Fatalf("fault=%+v err=%v", fault, err)
	}
	cleared, err := client.Databases.ClearDevelopmentFault(t.Context(), "dev")
	if err != nil || cleared.Active {
		t.Fatalf("clear=%+v err=%v", cleared, err)
	}
	logs, err := client.Databases.DevelopmentLogs(t.Context(), "dev", 100)
	if err != nil || !logs.Truncated || logs.Entries[0].Sequence != math.MaxUint64 || logs.Entries[0].AtMillis != math.MaxUint64 {
		t.Fatalf("logs=%+v err=%v", logs, err)
	}
	request.ConfirmBranch = "prod"
	if _, err := client.Databases.EnableDevelopmentFault(t.Context(), "dev", request); err == nil {
		t.Fatal("wrong branch confirmed")
	}
	request.ConfirmBranch = "dev"
	request.DurationMS = 30001
	if _, err := client.Databases.EnableDevelopmentFault(t.Context(), "dev", request); err == nil {
		t.Fatal("unbounded fault accepted")
	}
	if _, err := client.Databases.DevelopmentLogs(t.Context(), "dev", 101); err == nil {
		t.Fatal("unbounded logs accepted")
	}
	if calls != 3 {
		t.Fatalf("validation sent HTTP: %d calls", calls)
	}
}

func TestOperationsGetPollsWithoutRepeatingCreation(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != "GET" || r.URL.EscapedPath() != "/api/v1/operations/operation%2Fone" || r.Header.Get("Idempotency-Key") != "" {
			t.Errorf("operation discovery repeated work: %s %s", r.Method, r.URL)
		}
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{"operation_id":"operation/one","database_id":"db-1","key_version":"key-1","name":"demo","regions":["GRU"],"residency":"","state":"pending"}`))
	}))
	defer server.Close()
	client, err := NewClient(Config{APIBaseURL: server.URL})
	if err != nil {
		t.Fatal(err)
	}
	operation, err := client.Operations.Get(t.Context(), "operation/one")
	if err != nil {
		t.Fatal(err)
	}
	if operation.State != "pending" || operation.OperationID != "operation/one" {
		t.Fatalf("lost operation progress: %+v", operation)
	}
}

func TestExportDiscoveryAndCancellationPreserveProgress(t *testing.T) {
	const status = `{"job_id":"job-1","status":"Running","created_at_millis":18446744073709551615,"phase":"History","cancellation":"requested","pages":18446744073709551615,"records":9007199254740993,"estimated_records":null,"progress_basis_points":5000,"resume_cursor":"opaque","manifest":null,"error":null}`
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		switch r.Method + " " + r.URL.RequestURI() {
		case "GET /api/v1/databases/demo/exports?after=job-0&limit=50":
			_, _ = w.Write([]byte(`{"exports":[` + status + `],"next_cursor":"job-1"}`))
		case "POST /api/v1/databases/demo/exports/job-1/cancel":
			_, _ = w.Write([]byte(status))
		default:
			t.Errorf("wrong export lifecycle route: %s %s", r.Method, r.URL)
			w.WriteHeader(404)
		}
	}))
	defer server.Close()
	client, err := NewClient(Config{APIBaseURL: server.URL})
	if err != nil {
		t.Fatal(err)
	}
	list, err := client.Databases.ListExports(t.Context(), "demo", "job-0", 50)
	if err != nil {
		t.Fatal(err)
	}
	if list.NextCursor == nil || *list.NextCursor != "job-1" || list.Exports[0].Pages != math.MaxUint64 || list.Exports[0].CreatedAtMillis != math.MaxUint64 {
		t.Fatalf("discovery lost metadata: %+v", list)
	}
	cancelled, err := client.Databases.CancelExport(t.Context(), "demo", "job-1")
	if err != nil {
		t.Fatal(err)
	}
	if cancelled.Cancellation != ExportCancellationRequested || cancelled.Status != ExportRunning || cancelled.Phase != ExportHistory {
		t.Fatalf("cancel request incorrectly terminal: %+v", cancelled)
	}
	if _, err := client.Databases.ListExports(t.Context(), "demo", "", 51); err == nil {
		t.Fatal("unbounded list accepted")
	}
}

func TestUsageStorageIntervalCompletenessIsNullable(t *testing.T) {
	for _, test := range []struct {
		wire     string
		complete bool
	}{{`{"storage_complete_through_millis":null}`, false}, {`{"storage_complete_through_millis":18446744073709551615}`, true}} {
		var usage DatabaseUsage
		if err := json.Unmarshal([]byte(test.wire), &usage); err != nil {
			t.Fatal(err)
		}
		if test.complete {
			if usage.StorageCompleteThroughMillis == nil || *usage.StorageCompleteThroughMillis != math.MaxUint64 {
				t.Fatal("storage interval lost precision")
			}
		} else if usage.StorageCompleteThroughMillis != nil {
			t.Fatal("unknown storage interval became complete")
		}
	}
}

func TestProvisioningOperationMatchesProducerScopeAndState(t *testing.T) {
	operation := []byte(`{"environment":"production","tenant_id":"tenant-1","project_id":"project-1","operation_id":"op-1","database_id":"db-1","parent_database_id":null,"key_version":"key-1","name":"demo","regions":["GRU"],"residency":"br-only","state":"pending"}`)
	var got ProvisioningOperation
	if err := json.Unmarshal(operation, &got); err != nil {
		t.Fatal(err)
	}
	if got.Environment != "production" || got.TenantID != "tenant-1" || got.ProjectID != "project-1" || got.ParentDatabaseID != nil {
		t.Fatalf("provisioning scope = %+v", got)
	}
	var result CreateDatabaseResult
	if err := result.decodeHTTPResponse(http.StatusAccepted, operation); err != nil {
		t.Fatalf("canonical pending response rejected: %v", err)
	}
	if result.Operation == nil || result.Operation.State != "pending" {
		t.Fatalf("accepted operation = %+v", result.Operation)
	}
	titleCase := []byte(`{"environment":"production","tenant_id":"tenant-1","project_id":"project-1","operation_id":"op-1","database_id":"db-1","key_version":"key-1","name":"demo","regions":["GRU"],"residency":"br-only","state":"Pending"}`)
	if err := result.decodeHTTPResponse(http.StatusAccepted, titleCase); err == nil {
		t.Fatal("non-canonical title-case state was accepted")
	}
}

func TestProvisionedBranchAndDeletePreserveAcceptedOperation(t *testing.T) {
	const operation = `{"environment":"production","tenant_id":"tenant-1","project_id":"project-1","operation_id":"branch-key","database_id":"db-1","parent_database_id":null,"key_version":"key-1","name":"demo","regions":["GRU"],"residency":"br-only","state":"pending"}`
	const deletion = `{"request":{"environment":"production","tenant_id":"tenant-1","project_id":"project-1","database_id":"db-1","operation_id":"delete-key"},"state":"preparing","cutoff":"2026-09-06T00:00:00Z","targets":[{"stop":{}}]}`
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		if r.Method == http.MethodPost {
			if r.URL.Path != "/api/v1/databases/demo/branches" {
				t.Fatalf("branch path = %s", r.URL.Path)
			}
			var body CreateBranchRequest
			if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
				t.Fatal(err)
			}
			if body.OperationID != "branch-key" {
				t.Fatalf("branch operation id = %q", body.OperationID)
			}
			w.WriteHeader(http.StatusAccepted)
			_, _ = w.Write([]byte(operation))
			return
		}
		if r.URL.Path != "/api/v1/databases/demo" {
			t.Fatalf("delete path = %s", r.URL.Path)
		}
		w.WriteHeader(http.StatusAccepted)
		_, _ = w.Write([]byte(deletion))
	}))
	defer server.Close()
	client, err := NewClient(Config{APIBaseURL: server.URL})
	if err != nil {
		t.Fatal(err)
	}
	branch, err := client.DatabaseBranches.CreateBranch(t.Context(), "demo", CreateBranchRequest{Name: "child"}, WithIdempotencyKey("branch-key"))
	if err != nil || branch == nil || branch.OperationID != "branch-key" {
		t.Fatalf("branch result = %+v, err = %v", branch, err)
	}
	deleted, err := client.Databases.DeleteDatabase(t.Context(), "demo", WithIdempotencyKey("delete-key"), WithDeleteConfirmation("demo"))
	if err != nil || deleted == nil || deleted.Request.OperationID != "delete-key" {
		t.Fatalf("delete result = %+v, err = %v", deleted, err)
	}
}
