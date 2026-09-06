package sdk

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"math"
	"net/http"
	"net/http/httptest"
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
		{name: "webhooks-create", method: "POST", path: "/api/v1/databases/demo/webhooks", body: `{"endpoint":"https://hooks.example.test/events"}`, status: 201, response: endpoint, call: func(c *Client) (any, error) {
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
