package v1_test

import (
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"testing"

	tb "terrascale.tech/sdk/v1"
)

func TestAnonymousPublicProbes(t *testing.T) {
	for _, endpoint := range []string{"healthz", "readyz", "backupz", "health/replication"} {
		t.Run(endpoint, func(t *testing.T) {
			calls := 0
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				calls++
				if r.Method != http.MethodGet || r.URL.Path != "/proxy/"+endpoint || r.Header.Get("Authorization") != "" {
					t.Errorf("probe = %s %s with credentials=%t", r.Method, r.URL, r.Header.Get("Authorization") != "")
				}
				if endpoint == "health/replication" {
					io.WriteString(w, `{"region":"gru","status":"degraded","maxLagSeconds":0,"peers":[{"region":"sjc","status":"degraded","lagSeconds":null}]}`)
				} else {
					status := "ok"
					if endpoint != "healthz" {
						w.WriteHeader(http.StatusServiceUnavailable)
						status = "unavailable"
					}
					io.WriteString(w, `{"status":"`+status+`","version":"0.0.1","region":"gru","storageFormat":"17","revision":null,"machine":"local"}`)
				}
			}))
			t.Cleanup(server.Close)
			probes, err := tb.NewProbes(tb.Config{BaseURL: server.URL + "/proxy", HTTPClient: server.Client(), Token: "must-not-be-sent"})
			if err != nil {
				t.Fatal(err)
			}
			if endpoint == "health/replication" {
				out, err := probes.Replication(t.Context())
				if err != nil || out.Status != "degraded" || out.MaxLagSeconds == nil || *out.MaxLagSeconds != 0 || out.Peers[0].LagSeconds != nil {
					t.Fatalf("replication = %+v, %v", out, err)
				}
			} else {
				var out *tb.ProbeStatus
				switch endpoint {
				case "healthz":
					out, err = probes.Health(t.Context())
				case "readyz":
					out, err = probes.Ready(t.Context())
				case "backupz":
					out, err = probes.Backup(t.Context())
				}
				if endpoint == "healthz" {
					if err != nil {
						t.Fatal(err)
					}
				} else {
					var api *tb.APIError
					if !errors.As(err, &api) || api.Status != http.StatusServiceUnavailable {
						t.Fatalf("unavailable = %v", err)
					}
				}
				if out == nil || out.Version != "0.0.1" || out.StorageFormat != "17" || out.Revision != nil || out.Machine != "local" {
					t.Fatalf("status = %+v", out)
				}
			}
			if calls != 1 {
				t.Fatalf("requests = %d", calls)
			}
		})
	}
}

func TestPublicPlatformExploreContracts(t *testing.T) {
	const document = `{"id":"01ARZ3NDEKTSV4RRFFQ69G5FD6","version":"tbv2-opaque","state":"value","document":{"token":"[redacted]","count":9007199254740993}}`
	for _, endpoint := range []string{"databases", "collections", "documents", "point"} {
		t.Run(endpoint, func(t *testing.T) {
			path, query, reply := "/v1/platform/databases", "", `{"items":[{"name":"ts-auth","id":"`+parityID+`","homeRegion":"gru","replicaRegions":["sjc"]}]}`
			switch endpoint {
			case "collections":
				path, reply = path+"/ts-auth/collections", `{"items":[{"id":"`+parityID+`","name":"users","created_at":"1"}],"next_cursor":null}`
			case "documents":
				path, query, reply = path+"/ts-auth/collections/users/documents", "cursor=opaque%2B%2F%3D&limit=2", `{"items":[`+document+`],"next_cursor":"next"}`
			case "point":
				path, query, reply = path+"/ts-auth/collections/users/documents/by-key", "id=01ARZ3NDEKTSV4RRFFQ69G5FD6", document
			}
			c := parityClient(t, http.MethodGet, path, query, "", reply, http.StatusOK)
			switch endpoint {
			case "databases":
				out, err := c.PlatformDatabases(t.Context())
				if err != nil || len(out) != 1 || out[0].ID != parityID || out[0].Name != "ts-auth" {
					t.Fatalf("databases = %+v, %v", out, err)
				}
			case "collections":
				out, err := c.PlatformCollections(t.Context(), "ts-auth")
				if err != nil || out.Items[0].ID != parityID || out.Items[0].CreatedAt != 1 {
					t.Fatalf("collections = %+v, %v", out, err)
				}
			case "documents":
				out, err := c.PlatformDocuments(t.Context(), "ts-auth", "users", tb.ListOptions{Limit: 2, Cursor: "opaque+/="})
				if err != nil || out.Items[0].ID != "01ARZ3NDEKTSV4RRFFQ69G5FD6" || out.NextCursor != "next" {
					t.Fatalf("documents = %+v, %v", out, err)
				}
			case "point":
				out, err := c.PlatformDocument(t.Context(), "ts-auth", "users", "01ARZ3NDEKTSV4RRFFQ69G5FD6")
				if err != nil || out.ID != "01ARZ3NDEKTSV4RRFFQ69G5FD6" || string(out.Document) != `{"token":"[redacted]","count":9007199254740993}` {
					t.Fatalf("document = %+v, %v", out, err)
				}
			}
		})
	}
}

func TestPublicPlatformDenialPreservesCode(t *testing.T) {
	c := parityClient(t, http.MethodGet, "/v1/platform/databases", "", "", `{"error":{"code":"forbidden","message":"platform admin required"}}`, http.StatusForbidden)
	_, err := c.PlatformDatabases(t.Context())
	if !errors.Is(err, tb.ErrForbidden) {
		t.Fatalf("denial = %v", err)
	}
}
