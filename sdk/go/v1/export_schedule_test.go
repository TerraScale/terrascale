package v1_test

import (
	"context"
	"errors"
	"fmt"
	"net/http"
	"strings"
	"testing"
	"time"

	tb "terrascale.tech/sdk/v1"
)

const exportSchedulePath = parityDatabase + "/export-schedule"
const exportRunTimes = `"startedAt":"2026-10-24T03:00:00Z","finishedAt":"2026-10-24T03:00:01Z"`

func TestExportScheduleDefaultAndNullableFields(t *testing.T) {
	for _, body := range []string{`{}`, `{"cron":null,"updatedAt":null,"lastRun":null,"future":true}`} {
		c := parityClient(t, http.MethodGet, exportSchedulePath, "", "", body, 200)
		out, err := c.Database(parityProject, "main").ExportSchedule(t.Context())
		if err != nil || out.Cron != nil || out.UpdatedAt != nil || out.LastRun != nil {
			t.Fatalf("schedule=%+v error=%v", out, err)
		}
	}
	for _, cron := range []*string{nil, new(string), stringPointer("  "), stringPointer("0 3 * * *")} {
		body := `{"cron":null}`
		if cron != nil {
			body = fmt.Sprintf(`{"cron":%q}`, *cron)
		}
		c := parityClient(t, http.MethodPut, exportSchedulePath, "", body, `{"cron":null,"updatedAt":"2026-10-24T02:00:00Z"}`, 200)
		out, err := c.Database(parityProject, "main").SetExportSchedule(t.Context(), tb.ExportScheduleRequest{Cron: cron})
		if err != nil || out.UpdatedAt == nil || out.UpdatedAt.IsZero() || out.LastRun != nil {
			t.Fatalf("schedule=%+v error=%v", out, err)
		}
	}
}

func stringPointer(s string) *string { return &s }

func TestExportScheduleLastRunOutcomes(t *testing.T) {
	success := `{` + exportRunTimes + `,"status":"succeeded","key":"terrabase-exports/project/database/20261024T030000Z.ndjson","documents":9007199254740993,"bytes":9223372036854775807,"error":null}`
	for _, failure := range []string{"", "the project's backup destination is not set", "the export could not be written to the customer bucket", "disk_capacity: insufficient disk capacity"} {
		run := success
		if failure != "" {
			run = fmt.Sprintf(`{%s,"status":"failed","key":null,"documents":7,"error":%q}`, exportRunTimes, failure)
		}
		body := `{"cron":"0 3 * * *","updatedAt":"2026-10-24T02:00:00Z","lastRun":` + run + `}`
		c := parityClient(t, http.MethodGet, exportSchedulePath, "", "", body, 200)
		out, err := c.Database(parityProject, "main").ExportSchedule(t.Context())
		if err != nil {
			t.Fatal(err)
		}
		if out.Cron == nil || *out.Cron != "0 3 * * *" || out.LastRun == nil || out.LastRun.StartedAt.IsZero() || out.LastRun.FinishedAt.IsZero() {
			t.Fatal("lost camelCase schedule or run fields")
		}
		if failure == "" {
			if out.LastRun.Documents != 9007199254740993 || out.LastRun.Bytes != 9223372036854775807 || out.LastRun.Key == nil || *out.LastRun.Key != "terrabase-exports/project/database/20261024T030000Z.ndjson" || out.LastRun.Error != nil {
				t.Fatal("success lost exact counters or nullable fields")
			}
		} else if out.LastRun.Status != "failed" || out.LastRun.Key != nil || out.LastRun.Bytes != 0 || *out.LastRun.Error != failure {
			t.Fatal("failure lost sanitized error or zero defaults")
		}
	}
}

func TestExportScheduleAPIErrorsAndMutationNonReplay(t *testing.T) {
	for _, item := range []struct {
		status  int
		code    string
		message string
	}{
		{400, "invalid_request", "cron must be a standard 5-field cron expression"},
		{401, "unauthorized", "authentication required"}, {403, "forbidden", "role too low"},
		{404, "not_found", "not found"}, {409, "conflict", "configure the project's backup destination first"},
		{503, "catalog_stale", "catalog unavailable"},
	} {
		c := parityClient(t, http.MethodPut, exportSchedulePath, "", `{"cron":"daily"}`, fmt.Sprintf(`{"error":{"code":%q,"message":%q}}`, item.code, item.message), item.status)
		_, err := c.Database(parityProject, "main").SetExportSchedule(t.Context(), tb.ExportScheduleRequest{Cron: stringPointer("daily")})
		var api *tb.APIError
		if !errors.As(err, &api) || api.Status != item.status || string(api.Code) != item.code || api.Message != item.message {
			t.Fatalf("status=%d error=%v", item.status, err)
		}
	}
}

func TestStoppingExportScheduleRetainsLastRun(t *testing.T) {
	body := `{"cron":null,"lastRun":{` + exportRunTimes + `,"status":"failed","key":null,"documents":7,"bytes":0,"error":"the export could not be written to the customer bucket"}}`
	c := parityClient(t, http.MethodPut, exportSchedulePath, "", `{"cron":null}`, body, 200)
	out, err := c.Database(parityProject, "main").SetExportSchedule(t.Context(), tb.ExportScheduleRequest{})
	if err != nil || out.Cron != nil || out.LastRun == nil || out.LastRun.Status != "failed" || out.LastRun.Documents != 7 {
		t.Fatalf("schedule=%+v error=%v", out, err)
	}
}

func TestExportScheduleRejectsMalformedRunWithoutReplay(t *testing.T) {
	valid := `{` + exportRunTimes + `,"status":"succeeded","key":"object.ndjson","documents":1,"bytes":2}`
	for _, body := range []string{
		`null`, `[]`, `{"lastRun":{}}`,
		`{"lastRun":` + strings.Replace(valid, `"succeeded"`, `"pending"`, 1) + `}`,
		`{"lastRun":` + strings.Replace(valid, `"documents":1`, `"documents":-1`, 1) + `}`,
		`{"lastRun":` + strings.Replace(valid, `"bytes":2`, `"bytes":9223372036854775808`, 1) + `}`,
		`{"lastRun":` + strings.Replace(valid, `"object.ndjson"`, `null`, 1) + `}`,
	} {
		c := parityClient(t, http.MethodPut, exportSchedulePath, "", `{"cron":null}`, body, 200)
		if _, err := c.Database(parityProject, "main").SetExportSchedule(t.Context(), tb.ExportScheduleRequest{}); err == nil {
			t.Fatal("accepted malformed success")
		}
	}
}

type exportScheduleTransport func(*http.Request) (*http.Response, error)

func (f exportScheduleTransport) RoundTrip(r *http.Request) (*http.Response, error) { return f(r) }

func TestExportScheduleCancellationAndStrictProjectIdentity(t *testing.T) {
	started := make(chan struct{})
	calls := 0
	hc := &http.Client{Transport: exportScheduleTransport(func(r *http.Request) (*http.Response, error) {
		calls++
		close(started)
		<-r.Context().Done()
		return nil, r.Context().Err()
	})}
	c, err := tb.New(tb.Config{Token: "fixture.jwt.signature", HTTPClient: hc})
	if err != nil {
		t.Fatal(err)
	}
	db := c.Database(parityProject, "main")
	ctx, cancel := context.WithCancel(t.Context())
	cancel()
	if _, err := db.SetExportSchedule(ctx, tb.ExportScheduleRequest{}); !errors.Is(err, context.Canceled) {
		t.Fatal(err)
	}
	if _, err := c.Database("42", "main").ExportSchedule(t.Context()); err == nil || calls != 0 {
		t.Fatal("invalid identity reached transport")
	}
	ctx, cancel = context.WithTimeout(t.Context(), time.Second)
	done := make(chan error, 1)
	go func() { _, err := db.SetExportSchedule(ctx, tb.ExportScheduleRequest{}); done <- err }()
	<-started
	cancel()
	if err := <-done; !errors.Is(err, context.Canceled) || calls != 1 {
		t.Fatal(err)
	}
}
