package terrabase

import (
	"errors"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

// TestMeterReadsTheUsageRoutes checks the request the meter sends and that it
// decodes TerraBase's usage body (the TS-581 shape).
func TestMeterReadsTheUsageRoutes(t *testing.T) {
	var got *http.Request
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		got = r
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{"projectId":"p1","databases":1,"storageBytes":49152,"logicalBytes":1234,"documents":10,"reads":120,"writes":40,
			"period":{"from":"2026-10-05T17:00:00+00:00","to":"2026-10-06T17:00:00+00:00","granularity":"hour","reads":30,"writes":4},
			"series":[{"start":"2026-10-05T17:00:00+00:00","reads":3,"writes":1}],
			"items":[{"databaseId":"d1","logicalBytes":1234,"documents":10,"reads":120,"writes":40,"period":{"reads":30,"writes":4},"series":[]}]}`))
	}))
	defer server.Close()
	from := time.Date(2026, 10, 5, 17, 0, 0, 0, time.UTC)
	usage, err := Meter{BaseURL: server.URL + "/", Client: server.Client()}.Usage(t.Context(), "token-1", core.UsageScope{
		OrganizationID: "o 1", ProjectID: "p1", Granularity: "hour", From: from,
	})
	if err != nil {
		t.Fatal(err)
	}
	if got.URL.EscapedPath() != "/v1/management/organizations/o%201/projects/p1/usage" ||
		got.URL.Query().Get("granularity") != "hour" || got.URL.Query().Get("from") != "2026-10-05T17:00:00Z" ||
		got.Header.Get("Authorization") != "Bearer token-1" {
		t.Fatalf("request = %s %s, Authorization %q", got.Method, got.URL, got.Header.Get("Authorization"))
	}
	if usage.LogicalBytes != 1234 || usage.Period.Reads != 30 || usage.Period.Writes != 4 || len(usage.Series) != 1 ||
		!usage.Series[0].Start.Equal(from) || usage.Series[0].Reads != 3 || len(usage.Items) != 1 || usage.Items[0].DatabaseID != "d1" {
		t.Fatalf("usage = %+v", usage)
	}
}

func TestMeterMapsTerraBaseRefusals(t *testing.T) {
	for status, want := range map[int]error{
		http.StatusNotFound:            core.ErrNotMetered,
		http.StatusUnauthorized:        core.ErrUnauthorized,
		http.StatusBadRequest:          core.ErrUnavailable,
		http.StatusInternalServerError: core.ErrUnavailable,
	} {
		server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			w.WriteHeader(status)
		}))
		_, err := Meter{BaseURL: server.URL, Client: server.Client()}.Usage(t.Context(), "token", core.UsageScope{
			OrganizationID: "o", ProjectID: "p", DatabaseID: "d", Granularity: "day", From: time.Now(),
		})
		server.Close()
		if !errors.Is(err, want) {
			t.Errorf("status %d: error = %v, want %v", status, err, want)
		}
	}
	if _, err := (Meter{BaseURL: "http://unused"}).Usage(t.Context(), "token", core.UsageScope{DatabaseID: "d"}); !errors.Is(err, core.ErrInvalid) {
		t.Errorf("scope without an organization: error = %v", err)
	}
}
