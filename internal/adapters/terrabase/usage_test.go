package terrabase

import (
	"encoding/json/v2"
	"errors"
	"net/http"
	"net/http/httptest"
	"strconv"
	"strings"
	"testing"
	"time"

	"terrascale.tech/dashboard/internal/core"
)

// Project and database routes preserve the core fields through the SDK.
func TestMeterReadsTheUsageRoutes(t *testing.T) {
	for _, databaseID := range []string{"", "01ARZ3NDEKTSV4RRFFQ69G5FAW"} {
		t.Run("database="+databaseID, func(t *testing.T) {
			var got *http.Request
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				got = r
				w.Header().Set("Content-Type", "application/json")
				body := `{"projectId":"01ARZ3NDEKTSV4RRFFQ69G5FAX","databases":1,"storageBytes":49152,"logicalBytes":1234,"documents":10,"reads":120,"writes":40,
			"period":{"from":"2026-10-05T17:00:00+00:00","to":"2026-10-06T17:00:00+00:00","granularity":"hour","reads":30,"writes":4},
			"series":[{"start":"2026-10-05T17:00:00+00:00","reads":3,"writes":1}],
			"items":[{"databaseId":"01ARZ3NDEKTSV4RRFFQ69G5FAW","storageBytes":49152,"logicalBytes":1234,"documents":10,"reads":120,"writes":40,"period":{"reads":30,"writes":4},"series":[{"start":"2026-10-05T17:00:00+00:00","reads":3,"writes":1}]}]}`
				if databaseID != "" {
					body = strings.Replace(body, `"projectId":"01ARZ3NDEKTSV4RRFFQ69G5FAX"`, `"projectId":"01ARZ3NDEKTSV4RRFFQ69G5FAX","databaseId":"01ARZ3NDEKTSV4RRFFQ69G5FAW"`, 1)
				}
				_, _ = w.Write([]byte(body))
			}))
			defer server.Close()
			from := time.Date(2026, 10, 5, 17, 0, 0, 0, time.UTC)
			usage, err := Meter{BaseURL: server.URL + "/", Client: server.Client()}.Usage(t.Context(), "token-1", core.UsageScope{
				OrganizationID: "01ARZ3NDEKTSV4RRFFQ69G5FAV", ProjectID: "01ARZ3NDEKTSV4RRFFQ69G5FAX", DatabaseID: databaseID, Granularity: "hour", From: from,
			})
			if err != nil {
				t.Fatal(err)
			}
			wantPath := "/v1/management/organizations/01ARZ3NDEKTSV4RRFFQ69G5FAV/projects/01ARZ3NDEKTSV4RRFFQ69G5FAX/usage"
			if databaseID != "" {
				wantPath = "/v1/management/organizations/01ARZ3NDEKTSV4RRFFQ69G5FAV/projects/01ARZ3NDEKTSV4RRFFQ69G5FAX/databases/01ARZ3NDEKTSV4RRFFQ69G5FAW/usage"
			}
			if got.Method != http.MethodGet || got.URL.EscapedPath() != wantPath ||
				got.URL.Query().Get("granularity") != "hour" || got.URL.Query().Get("from") != "2026-10-05T17:00:00Z" ||
				got.Header.Get("Authorization") != "Bearer token-1" {
				t.Fatalf("request = %s %s, Authorization %q", got.Method, got.URL, got.Header.Get("Authorization"))
			}
			point := core.UsagePoint{Start: from, Reads: 3, Writes: 1}
			want := core.Usage{
				ProjectID: "01ARZ3NDEKTSV4RRFFQ69G5FAX", DatabaseID: databaseID, StorageBytes: 49152, LogicalBytes: 1234, Documents: 10, Reads: 120, Writes: 40,
				Period: core.UsagePeriod{From: from, To: from.Add(24 * time.Hour), Granularity: "hour", Reads: 30, Writes: 4},
				Series: []core.UsagePoint{point},
				Items: []core.Usage{{DatabaseID: "01ARZ3NDEKTSV4RRFFQ69G5FAW", StorageBytes: 49152, LogicalBytes: 1234, Documents: 10, Reads: 120, Writes: 40,
					Period: core.UsagePeriod{Reads: 30, Writes: 4}, Series: []core.UsagePoint{point}}},
			}
			gotJSON, err := json.Marshal(usage)
			if err != nil {
				t.Fatal(err)
			}
			wantJSON, err := json.Marshal(want)
			if err != nil {
				t.Fatal(err)
			}
			if string(gotJSON) != string(wantJSON) {
				t.Fatalf("usage = %s, want %s", gotJSON, wantJSON)
			}
		})
	}
}

func TestMeterMapsTerraBaseRefusals(t *testing.T) {
	for status, want := range map[int]error{
		http.StatusNotFound:            core.ErrNotMetered,
		http.StatusUnauthorized:        core.ErrUnauthorized,
		http.StatusBadRequest:          core.ErrUnavailable,
		http.StatusForbidden:           core.ErrUnavailable,
		http.StatusInternalServerError: core.ErrUnavailable,
	} {
		t.Run(strconv.Itoa(status), func(t *testing.T) {
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				w.WriteHeader(status)
			}))
			_, err := Meter{BaseURL: server.URL, Client: server.Client()}.Usage(t.Context(), "token", core.UsageScope{
				OrganizationID: "01ARZ3NDEKTSV4RRFFQ69G5FAV", ProjectID: "01ARZ3NDEKTSV4RRFFQ69G5FAX", DatabaseID: "01ARZ3NDEKTSV4RRFFQ69G5FAW", Granularity: "day", From: time.Now(),
			})
			server.Close()
			if !errors.Is(err, want) {
				t.Errorf("status %d: error = %v, want %v", status, err, want)
			}
		})
	}
}

func TestMeterRequiresOrganizationAndProject(t *testing.T) {
	var calls int
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { calls++ }))
	defer server.Close()
	for _, scope := range []core.UsageScope{
		{ProjectID: "01ARZ3NDEKTSV4RRFFQ69G5FAX"},
		{OrganizationID: "01ARZ3NDEKTSV4RRFFQ69G5FAV"},
		{OrganizationID: "01ARZ3NDEKTSV4RRFFQ69G5FAV", DatabaseID: "01ARZ3NDEKTSV4RRFFQ69G5FAW"},
		{OrganizationID: " ", ProjectID: "01ARZ3NDEKTSV4RRFFQ69G5FAX"},
		{OrganizationID: "01ARZ3NDEKTSV4RRFFQ69G5FAV", ProjectID: " "},
	} {
		if _, err := (Meter{BaseURL: server.URL, Client: server.Client()}).Usage(t.Context(), "token", scope); !errors.Is(err, core.ErrInvalid) {
			t.Errorf("scope %+v: error = %v, want ErrInvalid", scope, err)
		}
	}
	if calls != 0 {
		t.Errorf("invalid scopes made %d requests", calls)
	}
}

func TestMeterRejectsUnusableUsage(t *testing.T) {
	for _, test := range []struct {
		name, body string
		want       error
	}{
		{"lifetime only", `{"reads":120,"writes":40,"logicalBytes":1234}`, core.ErrNotMetered},
		{"null period", `{"reads":120,"writes":40,"period":null}`, core.ErrNotMetered},
		{"lifetime-only item", `{"period":{},"items":[{"reads":120,"writes":40}]}`, core.ErrNotMetered},
		{"negative lifetime reads", `{"reads":-1,"period":{}}`, core.ErrUnavailable},
		{"negative lifetime writes", `{"writes":-1,"period":{}}`, core.ErrUnavailable},
		{"negative storage", `{"storageBytes":-1,"period":{}}`, core.ErrUnavailable},
		{"negative logical storage", `{"logicalBytes":-1,"period":{}}`, core.ErrUnavailable},
		{"negative documents", `{"documents":-1,"period":{}}`, core.ErrUnavailable},
		{"negative period reads", `{"period":{"reads":-1}}`, core.ErrUnavailable},
		{"negative period writes", `{"period":{"writes":-1}}`, core.ErrUnavailable},
		{"negative series reads", `{"period":{},"series":[{"reads":-1}]}`, core.ErrUnavailable},
		{"negative series writes", `{"period":{},"series":[{"writes":-1}]}`, core.ErrUnavailable},
		{"negative item storage", `{"period":{},"items":[{"logicalBytes":-1,"period":{}}]}`, core.ErrUnavailable},
		{"negative item period", `{"period":{},"items":[{"period":{"writes":-1}}]}`, core.ErrUnavailable},
		{"negative nested series", `{"period":{},"items":[{"period":{},"series":[{"reads":-1}]}]}`, core.ErrUnavailable},
		{"malformed json", `{`, core.ErrUnavailable},
	} {
		t.Run(test.name, func(t *testing.T) {
			server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
				w.Header().Set("Content-Type", "application/json")
				_, _ = w.Write([]byte(test.body))
			}))
			defer server.Close()
			usage, err := (Meter{BaseURL: server.URL, Client: server.Client()}).Usage(t.Context(), "token", core.UsageScope{
				OrganizationID: "01ARZ3NDEKTSV4RRFFQ69G5FAV", ProjectID: "01ARZ3NDEKTSV4RRFFQ69G5FAX", Granularity: "day", From: time.Date(2026, 10, 1, 0, 0, 0, 0, time.UTC),
			})
			if !errors.Is(err, test.want) {
				t.Errorf("error = %v, want %v (usage = %+v)", err, test.want, usage)
			}
		})
	}
}

func TestMeterAcceptsMeasuredZeroUsage(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		_, _ = w.Write([]byte(`{"reads":120,"writes":40,"period":{"reads":0,"writes":0}}`))
	}))
	defer server.Close()
	usage, err := (Meter{BaseURL: server.URL, Client: server.Client()}).Usage(t.Context(), "token", core.UsageScope{
		OrganizationID: "01ARZ3NDEKTSV4RRFFQ69G5FAV", ProjectID: "01ARZ3NDEKTSV4RRFFQ69G5FAX", Granularity: "day", From: time.Date(2026, 10, 1, 0, 0, 0, 0, time.UTC),
	})
	if err != nil || usage.Period.Reads != 0 || usage.Period.Writes != 0 || usage.Reads != 120 || usage.Writes != 40 {
		t.Fatalf("usage = %+v, error = %v; want an empty measured period with lifetime counters preserved", usage, err)
	}
}
