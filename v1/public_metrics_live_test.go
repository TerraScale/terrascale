package v1_test

import (
	"io"
	"net/http"
	"net/http/httptest"
	"net/url"
	"testing"

	tb "terrascale.tech/sdk/v1"
)

func TestPublicProjectAndDatabaseMetrics(t *testing.T) {
	for _, scope := range []string{"project", "database"} {
		for _, endpoint := range []string{"analytics", "replication"} {
			t.Run(scope+"/"+endpoint, func(t *testing.T) {
				path, query := parityDatabase, "range=1h&region=gru"
				if scope == "project" {
					path, query = "/v1/projects/"+parityProject, "database="+parityID+"&"+query
				}
				body := analyticsFixture
				if endpoint == "replication" {
					body = replicationFixture
				}
				c := parityClient(t, http.MethodGet, path+"/"+endpoint, query, "", body, http.StatusOK)
				opts := tb.MetricsOptions{Range: tb.Range1Hour, Region: "gru", DatabaseID: parityID}
				if endpoint == "analytics" {
					var out *tb.AnalyticsResponse
					var err error
					if scope == "project" {
						out, err = c.ProjectAnalytics(t.Context(), parityProject, opts)
					} else {
						out, err = c.Database(parityProject, "main").Analytics(t.Context(), opts)
					}
					if err != nil || out.Items[0].Series[0].Operations["read"] != 9007199254740993 || out.Items[0].Series[0].StorageBytes != nil {
						t.Fatalf("analytics = %+v, %v", out, err)
					}
				} else {
					var out *tb.ReplicationResponse
					var err error
					if scope == "project" {
						out, err = c.ProjectReplication(t.Context(), parityProject, opts)
					} else {
						out, err = c.Database(parityProject, "main").Replication(t.Context(), opts)
					}
					if err != nil || out.Items[0].ReplicatedOps != 9007199254740993 || out.Items[1].Divergence != nil {
						t.Fatalf("replication = %+v, %v", out, err)
					}
				}
			})
		}
	}
}

func TestShapeManifestContract(t *testing.T) {
	const handle = "h1-0123456789abcdef"
	calls := 0
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		calls++
		q := r.URL.Query()
		if r.Method != http.MethodGet || r.URL.Path != parityDatabase+"/collections/users/shape/manifest" || q.Get("handle") != handle || q.Get("offset") != "snap.2.0" || q.Get("where") != `{"eq":["/active",true]}` || len(q) != 3 || r.Header.Get("Cache-Control") != "no-store" || r.Header.Get("Authorization") != "Bearer header.project.signature" {
			t.Errorf("request = %s %s", r.Method, r.URL)
		}
		w.Header().Set("Cache-Control", "private, no-store")
		io.WriteString(w, `{"handle":"`+handle+`","objects":[{"offset":"snap.2.0","next_offset":"2","binding":"binding","object":"opaque-object","url":"https://mirror.example.test/shape?signature=fake","expires_at":"2026-10-22T00:00:00Z","epoch":"123","key_id":"`+parityID+`","sha256":"abc","size":128}]}`)
	}))
	t.Cleanup(server.Close)
	c, err := tb.New(tb.Config{BaseURL: server.URL, Token: "header.project.signature", HTTPClient: server.Client()})
	if err != nil {
		t.Fatal(err)
	}
	col := c.Database(parityProject, "main").Collection("users")
	out, err := col.ShapeManifest(t.Context(), handle, "snap.2.0", tb.Eq("/active", true))
	if err != nil {
		t.Fatal(err)
	}
	if out.Objects[0].NextOffset != "2" || out.Objects[0].KeyID != parityID || out.Objects[0].Epoch != "123" || out.Objects[0].Size != 128 || out.Objects[0].ExpiresAt.IsZero() {
		t.Error("incorrect manifest decoding")
	}
	if parsed, err := url.Parse(out.Objects[0].URL); err != nil || parsed.Host != "mirror.example.test" {
		t.Error("incorrect mirror URL")
	}
	if _, err := col.ShapeManifest(t.Context(), handle, "-1", tb.Filter{}); err == nil {
		t.Error("bootstrap offset accepted")
	}
	if calls != 1 {
		t.Fatalf("requests = %d; manifest URLs must not be fetched automatically", calls)
	}
}
