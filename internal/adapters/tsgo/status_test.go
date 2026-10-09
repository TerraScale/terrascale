package tsgo

import (
	"net/http"
	"net/http/httptest"
	"testing"
)

func TestStatusRegionsUsesCredentiallessSDKAndPreservesMissingAndZero(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/api/latency" || r.Header.Get("Authorization") != "" {
			t.Errorf("unexpected status request path or authorization")
		}
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{"window":"30m","regions":[{"code":"gru","status":"operational","consensus":{"median_ms":0,"samples":2}},{"code":"sfo","status":"degraded","consensus":{"median_ms":null,"samples":0}},{"code":"fra","status":"down"}]}`))
	}))
	defer server.Close()
	reader, err := NewStatusRegions(server.URL)
	if err != nil {
		t.Fatal(err)
	}
	rows, err := reader.RegionHealth(t.Context(), []string{"GRU", "SFO"})
	if err != nil {
		t.Fatal(err)
	}
	if len(rows) != 2 || rows[0].Region != "GRU" || rows[0].Status != "operational" || rows[0].LatencyMS == nil || *rows[0].LatencyMS != 0 || rows[1].LatencyMS != nil {
		t.Fatalf("regions=%+v", rows)
	}
}
