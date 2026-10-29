package v1_test

import (
	"encoding/json"
	"net/http"
	"net/url"
	"testing"
	"time"

	v1 "terrascale.tech/sdk/v1"
)

func TestUsageRejectsInvalidWindowBeforeSendingCredentials(t *testing.T) {
	m, err := v1.NewManagement(v1.Config{BaseURL: "https://tb.example", HTTPClient: &http.Client{Transport: failTransport{t}}})
	if err != nil {
		t.Fatal(err)
	}
	u := m.WithToken("user-token")
	now := time.Now()
	for _, tc := range []struct {
		project string
		opts    v1.UsageOptions
	}{
		{"", v1.UsageOptions{}},
		{testProject, v1.UsageOptions{Granularity: "minute"}},
		{testProject, v1.UsageOptions{From: now, To: now}},
		{testProject, v1.UsageOptions{From: now, To: now.Add(-time.Hour)}},
		{"../other", v1.UsageOptions{}},
	} {
		if _, err := u.Usage(t.Context(), testOrg, tc.project, "", tc.opts); err == nil {
			t.Fatalf("accepted project=%q window=%+v", tc.project, tc.opts)
		}
	}
}

func TestUsageDistinguishesMissingPeriodFromMeasuredZero(t *testing.T) {
	var old, current v1.Usage
	if err := json.Unmarshal([]byte(`{"reads":500,"writes":200}`), &old); err != nil {
		t.Fatal(err)
	}
	if err := json.Unmarshal([]byte(`{"reads":500,"writes":200,"logicalBytes":123,"period":{"from":"2026-10-01T00:00:00Z","to":"2026-10-07T00:00:00Z","granularity":"day","reads":0,"writes":0},"items":[{"databaseId":"01ARZ3NDEKTSV4RRFFQ69G5FA5","period":{"reads":1,"writes":2}}]}`), &current); err != nil {
		t.Fatal(err)
	}
	if old.Period != nil || current.Period == nil || current.Period.Reads != 0 || current.Period.From.Day() != 1 || current.LogicalBytes != 123 || len(current.Items) != 1 || current.Items[0].Period.Writes != 2 {
		t.Fatalf("old=%+v current=%+v", old, current)
	}
}

func TestUsagePreservesFractionalWindowBoundaries(t *testing.T) {
	capture := &captureTransport{}
	m, err := v1.NewManagement(v1.Config{BaseURL: "https://tb.example", HTTPClient: &http.Client{Transport: capture}, MaxRetries: -1})
	if err != nil {
		t.Fatal(err)
	}
	from := time.Date(2026, 10, 7, 10, 0, 0, 100_000_000, time.FixedZone("offset", 3600))
	to := from.Add(800 * time.Millisecond)
	_, _ = m.WithToken("user-token").Usage(t.Context(), testOrg, testProject, "", v1.UsageOptions{Granularity: "hour", From: from, To: to})
	if len(capture.queries) == 0 {
		t.Fatal("no usage request")
	}
	query, err := url.ParseQuery(capture.queries[0])
	if err != nil || query.Get("from") != "2026-10-07T09:00:00.1Z" || query.Get("to") != "2026-10-07T09:00:00.9Z" {
		t.Fatalf("query=%v error=%v", query, err)
	}
}
