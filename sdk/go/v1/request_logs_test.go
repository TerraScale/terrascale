package v1_test

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"reflect"
	"strings"
	"testing"

	tb "terrascale.tech/sdk/v1"
)

const requestLog = `{"id":"` + parityID + `","time":"2026-10-26T12:34:56Z","region":"gru","method":"GET","route":"/v1/projects/{project}/databases/{database}/collections/{collection}/documents/{key}","status":200,"latencyMs":0,"operations":0,"isQuery":false}`
const requestLogsEmpty = `{"databaseId":"` + parityID + `","items":[],"truncated":false,"recordingLimited":false}`

func TestRequestLogsRouteAndMetadata(t *testing.T) {
	for _, status := range []string{"", "2xx", "3xx", "4xx", "5xx"} {
		t.Run(status, func(t *testing.T) {
			query := ""
			if status != "" {
				query = "status=" + status
			}
			body := strings.Replace(requestLogsEmpty, `"items":[]`, `"items":[`+requestLog+`]`, 1)
			body = strings.Replace(body, `"recordingLimited":false`, `"recordingLimited":true`, 1)
			c := parityClient(t, http.MethodGet, parityDatabase+"/logs", query, "", body, 200)
			v, err := c.Database(parityProject, "main").RequestLogs(t.Context(), status)
			if err != nil || v.DatabaseID != parityID || len(v.Items) != 1 || !v.RecordingLimited || v.Truncated || v.Items[0].RequestID != nil || v.Items[0].IsQuery || v.Items[0].Operations != 0 || v.Items[0].LatencyMs != 0 {
				t.Fatalf("response=%+v error=%v", v, err)
			}
		})
	}
	for _, requestID := range []string{`null`, `"opaque-correlation"`} {
		var l tb.DatabaseRequestLog
		if err := json.Unmarshal([]byte(strings.TrimSuffix(requestLog, "}")+`,"requestId":`+requestID+`}`), &l); err != nil || (l.RequestID == nil) != (requestID == "null") {
			t.Fatalf("requestId=%s decoded=%+v error=%v", requestID, l, err)
		}
	}
}

func TestRequestLogsEmptyPageAndExactCounters(t *testing.T) {
	c := parityClient(t, http.MethodGet, parityDatabase+"/logs", "", "", requestLogsEmpty, 200)
	page, err := c.Database(parityProject, "main").RequestLogs(t.Context(), "")
	if err != nil || page.Items == nil || len(page.Items) != 0 || page.Truncated || page.RecordingLimited {
		t.Fatalf("response=%+v error=%v", page, err)
	}
	var entry tb.DatabaseRequestLog
	exact := strings.Replace(requestLog, `"operations":0`, `"operations":9223372036854775807`, 1)
	if err = json.Unmarshal([]byte(exact), &entry); err != nil || entry.Operations != 9223372036854775807 {
		t.Fatalf("counter lost precision: %+v %v", entry, err)
	}
	before := entry
	if json.Unmarshal([]byte(strings.Replace(exact, `"isQuery":false`, `"isQuery":null`, 1)), &entry) == nil || !reflect.DeepEqual(entry, before) {
		t.Fatal("invalid entry changed receiver")
	}
	oversized := strings.Replace(requestLogsEmpty, `"items":[]`, `"items":[`+strings.Repeat(requestLog+",", 100)+requestLog+`]`, 1)
	if json.Unmarshal([]byte(oversized), &page) == nil {
		t.Fatal("accepted more than100 observations")
	}
}

func TestRequestLogsRejectMalformedAtomically(t *testing.T) {
	original := tb.DatabaseRequestLogs{DatabaseID: parityProject, RecordingLimited: true}
	var valid map[string]json.RawMessage
	if err := json.Unmarshal([]byte(requestLogsEmpty), &valid); err != nil {
		t.Fatal(err)
	}
	for _, field := range []string{"databaseId", "items", "truncated", "recordingLimited"} {
		for _, omitted := range []bool{true, false} {
			copy := make(map[string]json.RawMessage)
			for k, v := range valid {
				copy[k] = v
			}
			if omitted {
				delete(copy, field)
			} else {
				copy[field] = json.RawMessage("null")
			}
			body, _ := json.Marshal(copy)
			got := original
			if json.Unmarshal(body, &got) == nil || !reflect.DeepEqual(got, original) {
				t.Fatalf("accepted missing/null %s or changed receiver", field)
			}
		}
	}
	for _, bad := range []string{"null", `{}`, strings.Replace(requestLogsEmpty, `"items":[]`, `"items":[null]`, 1), strings.Replace(requestLogsEmpty, parityID, "not-ulid", 1)} {
		got := original
		if json.Unmarshal([]byte(bad), &got) == nil || !reflect.DeepEqual(got, original) {
			t.Fatalf("accepted %s or changed receiver", bad)
		}
	}
	var entry map[string]json.RawMessage
	if err := json.Unmarshal([]byte(requestLog), &entry); err != nil {
		t.Fatal(err)
	}
	for field := range entry {
		for _, omitted := range []bool{true, false} {
			copy := make(map[string]json.RawMessage)
			for k, v := range entry {
				copy[k] = v
			}
			if omitted {
				delete(copy, field)
			} else {
				copy[field] = json.RawMessage("null")
			}
			body, _ := json.Marshal(copy)
			var got tb.DatabaseRequestLog
			if json.Unmarshal(body, &got) == nil {
				t.Fatalf("accepted missing/null entry %s", field)
			}
		}
	}
	for _, bad := range []string{strings.Replace(requestLog, parityID, "bad", 1), strings.Replace(requestLog, `"operations":0`, `"operations":-1`, 1), strings.Replace(requestLog, `"status":200`, `"status":600`, 1), strings.Replace(requestLog, `"latencyMs":0`, `"latencyMs":-1`, 1)} {
		var got tb.DatabaseRequestLog
		if json.Unmarshal([]byte(bad), &got) == nil {
			t.Fatal("accepted malformed entry")
		}
	}
}

func TestRequestLogsRefusalAndCancellation(t *testing.T) {
	for _, status := range []int{400, 403, 404} {
		c := parityClient(t, http.MethodGet, parityDatabase+"/logs", "", "", `{"error":{"code":"not_found"}}`, status)
		v, err := c.Database(parityProject, "main").RequestLogs(t.Context(), "")
		var api *tb.APIError
		if v != nil || !errors.As(err, &api) || api.Status != status {
			t.Fatalf("response=%+v error=%v", v, err)
		}
	}
	c, err := tb.New(tb.Config{Token: "fixture.jwt.signature", HTTPClient: &http.Client{Transport: collectionRulesTransport(func(*http.Request) (*http.Response, error) {
		t.Error("unexpected dispatch")
		return nil, errors.New("unexpected")
	})}})
	if err != nil {
		t.Fatal(err)
	}
	d := c.Database(parityProject, "main")
	if _, err = d.RequestLogs(t.Context(), "200"); err == nil {
		t.Fatal("accepted invalid status")
	}
	ctx, cancel := context.WithCancel(t.Context())
	cancel()
	if _, err = d.RequestLogs(ctx, ""); !errors.Is(err, context.Canceled) {
		t.Fatal(err)
	}
}

func TestRequestLogsRejectUnexpectedSuccessProtocol(t *testing.T) {
	for _, test := range []struct {
		status int
		media  string
	}{{201, "application/json"}, {202, "application/json"}, {200, "text/plain"}, {200, ""}} {
		t.Run(test.media+http.StatusText(test.status), func(t *testing.T) {
			calls := 0
			c, err := tb.New(tb.Config{Token: "fixture.jwt.signature", HTTPClient: &http.Client{Transport: collectionRulesTransport(func(*http.Request) (*http.Response, error) {
				calls++
				return &http.Response{StatusCode: test.status, Header: http.Header{"Content-Type": {test.media}}, Body: io.NopCloser(strings.NewReader(requestLogsEmpty))}, nil
			})}})
			if err != nil {
				t.Fatal(err)
			}
			got, err := c.Database(parityProject, "main").RequestLogs(t.Context(), "")
			if got != nil || err == nil || calls != 1 {
				t.Fatalf("unexpected protocol accepted: result=%+v err=%v calls=%d", got, err, calls)
			}
		})
	}
}
